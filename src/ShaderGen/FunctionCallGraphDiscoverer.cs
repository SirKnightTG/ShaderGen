﻿using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using Microsoft.CodeAnalysis;
using System.Linq;
using System.Diagnostics;

namespace ShaderGen
{
    internal class FunctionCallGraphDiscoverer
    {
        public Compilation Compilation { get; }
        private CallGraphNode _rootNode;
        private Dictionary<TypeAndMethodName, CallGraphNode> _nodesByName = new Dictionary<TypeAndMethodName, CallGraphNode>();

        public FunctionCallGraphDiscoverer(Compilation compilation, TypeAndMethodName rootMethod)
        {
            Compilation = compilation;
            _rootNode = new CallGraphNode() { Name = rootMethod };
            bool foundDecl = GetDeclaration(rootMethod, out _rootNode.Declaration);
            Debug.Assert(foundDecl);
            _nodesByName.Add(rootMethod, _rootNode);
        }

        public ShaderFunctionAndBlockSyntax[] GetOrderedCallList()
        {
            HashSet<ShaderFunctionAndBlockSyntax> result = new HashSet<ShaderFunctionAndBlockSyntax>();
            TraverseNode(result, _rootNode);
            return result.ToArray();
        }

        private SemanticModel GetModel(SyntaxNode node) => Compilation.GetSemanticModel(node.SyntaxTree);

        private void TraverseNode(HashSet<ShaderFunctionAndBlockSyntax> result, CallGraphNode node)
        {
            foreach (ShaderFunctionAndBlockSyntax existing in result)
            {
                if (node.Parents.Any(cgn => cgn.Name.Equals(existing)))
                {
                    throw new ShaderGenerationException("There was a cyclical call graph involving " + existing + " and " + node.Name);
                }
            }

            foreach (CallGraphNode child in node.Children)
            {
                TraverseNode(result, child);
            }

            List<ParameterDefinition> parameters = new List<ParameterDefinition>();
            foreach (ParameterSyntax ps in node.Declaration.ParameterList.Parameters)
            {
                parameters.Add(ParameterDefinition.GetParameterDefinition(Compilation, ps));
            }

            TypeReference returnType = new TypeReference(GetModel(node.Declaration).GetFullTypeName(node.Declaration.ReturnType));

            UInt3 computeGroupCounts = new UInt3();
            bool isFragmentShader = false, isComputeShader = false;
            bool isVertexShader = Utilities.GetMethodAttributes(node.Declaration, "VertexShader").Any();
            if (!isVertexShader)
            {
                isFragmentShader = Utilities.GetMethodAttributes(node.Declaration, "FragmentShader").Any();
            }
            if (!isVertexShader && !isFragmentShader)
            {
                AttributeSyntax computeShaderAttr = Utilities.GetMethodAttributes(node.Declaration, "ComputeShader").FirstOrDefault();
                if (computeShaderAttr != null)
                {
                    isComputeShader = true;
                    computeGroupCounts.X = GetAttributeArgumentUIntValue(computeShaderAttr, 0);
                    computeGroupCounts.Y = GetAttributeArgumentUIntValue(computeShaderAttr, 1);
                    computeGroupCounts.Z = GetAttributeArgumentUIntValue(computeShaderAttr, 2);
                }
            }

            ShaderFunctionType type = isVertexShader
                ? ShaderFunctionType.VertexEntryPoint
                : isFragmentShader
                    ? ShaderFunctionType.FragmentEntryPoint
                    : isComputeShader
                        ? ShaderFunctionType.ComputeEntryPoint
                        : ShaderFunctionType.Normal;

            ShaderFunction sf = new ShaderFunction(
                node.Name.TypeName,
                node.Name.MethodName,
                returnType,
                parameters.ToArray(),
                type,
                computeGroupCounts);
            ShaderFunctionAndBlockSyntax sfab = new ShaderFunctionAndBlockSyntax(sf, node.Declaration.Body);

            result.Add(sfab);
        }

        private static uint GetAttributeArgumentUIntValue(AttributeSyntax attr, int index)
        {
            if (attr.ArgumentList.Arguments.Count < index + 1)
            {
                throw new ShaderGenerationException(
                    "Too few arguments in attribute " + attr.ToFullString() + ". Required + " + (index + 1));
            }
            string fullArg0 = attr.ArgumentList.Arguments[index].ToFullString();
            if (uint.TryParse(fullArg0, out uint ret))
            {
                return ret;
            }
            else
            {
                throw new ShaderGenerationException("Incorrectly formatted attribute: " + attr.ToFullString());
            }
        }

        public void GenerateFullGraph()
        {
            ExploreCallNode(_rootNode);
        }

        private void ExploreCallNode(CallGraphNode node)
        {
            Debug.Assert(node.Declaration != null);
            MethodWalker walker = new MethodWalker(this);
            walker.Visit(node.Declaration);
            TypeAndMethodName[] childrenNames = walker.GetChildren();
            foreach (TypeAndMethodName childName in childrenNames)
            {
                CallGraphNode childNode = GetNode(childName);
                if (childNode.Declaration != null)
                {
                    childNode.Parents.Add(node);
                    node.Children.Add(childNode);
                    ExploreCallNode(childNode);
                }
            }
        }

        private CallGraphNode GetNode(TypeAndMethodName name)
        {
            if (!_nodesByName.TryGetValue(name, out CallGraphNode node))
            {
                node = new CallGraphNode() { Name = name };
                GetDeclaration(name, out node.Declaration);
                _nodesByName.Add(name, node);
            }

            return node;
        }

        private bool GetDeclaration(TypeAndMethodName name, out MethodDeclarationSyntax decl)
        {
            INamedTypeSymbol symb = Compilation.GetTypeByMetadataName(name.TypeName);
            foreach (SyntaxReference synRef in symb.DeclaringSyntaxReferences)
            {
                SyntaxNode node = synRef.GetSyntax();
                foreach (SyntaxNode child in node.ChildNodes())
                {
                    if (child is MethodDeclarationSyntax mds)
                    {
                        if (mds.Identifier.ToFullString() == name.MethodName)
                        {
                            decl = mds;
                            return true;
                        }
                    }
                }
            }

            decl = null;
            return false;
        }

        private class MethodWalker : CSharpSyntaxWalker
        {
            private readonly FunctionCallGraphDiscoverer _discoverer;
            private readonly HashSet<TypeAndMethodName> _children = new HashSet<TypeAndMethodName>();

            public MethodWalker(FunctionCallGraphDiscoverer discoverer)
            {
                _discoverer = discoverer;
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (node.Expression is IdentifierNameSyntax ins)
                {
                    SymbolInfo symbolInfo = _discoverer.Compilation.GetSemanticModel(node.SyntaxTree).GetSymbolInfo(ins);
                    string containingType = symbolInfo.Symbol.ContainingType.ToDisplayString();
                    string methodName = symbolInfo.Symbol.Name;
                    _children.Add(new TypeAndMethodName() { TypeName = containingType, MethodName = methodName });
                    return;
                }
                else if (node.Expression is MemberAccessExpressionSyntax maes)
                {
                    SymbolInfo methodSymbol = _discoverer.Compilation.GetSemanticModel(maes.SyntaxTree).GetSymbolInfo(maes);
                    if (methodSymbol.Symbol is IMethodSymbol ims)
                    {
                        string containingType = Utilities.GetFullMetadataName(ims.ContainingType);
                        string methodName = ims.MetadataName;
                        _children.Add(new TypeAndMethodName() { TypeName = containingType, MethodName = methodName });
                        return;
                    }
                }

                throw new NotImplementedException();
            }

            public TypeAndMethodName[] GetChildren() => _children.ToArray();
        }
    }

    internal class CallGraphNode
    {
        public TypeAndMethodName Name;
        /// <summary>
        /// May be null.
        /// </summary>
        public MethodDeclarationSyntax Declaration;
        /// <summary>
        /// Functions called by this function.
        /// </summary>
        public HashSet<CallGraphNode> Children = new HashSet<CallGraphNode>();
        public HashSet<CallGraphNode> Parents = new HashSet<CallGraphNode>();
    }
}
