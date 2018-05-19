﻿using System;
using System.Collections.Generic;
using System.Linq;
using ShaderGen.Tests.Tools;
using Xunit;

namespace ShaderGen.Tests.Attributes
{
    /// <summary>
    /// Marking a test with this <see cref="TheoryAttribute"/> override will cause it to be skipped if
    /// the tool chains for the specified <see cref="Backends"/> are not available.
    /// </summary>
    /// <seealso cref="Xunit.TheoryAttribute" />
    public class BackendTheoryAttribute : TheoryAttribute
    {
        /// <summary>
        /// The backends that are required to run this test.
        /// </summary>
        public readonly IEnumerable<Type> Backends;

        /// <inheritdoc />
        public override string Skip
        {
            get
            {
                if (!string.IsNullOrEmpty(base.Skip))
                    return base.Skip;

                // Get a list of all backends that are not available
                IReadOnlyList<string> missingBackends = Backends
                    .Select(ToolChain.Get)
                    .Where(t => t?.IsAvailable == false)
                    .Select(t => t.Name)
                    .ToArray();

                if (missingBackends.Count < 1) return null;

                string last = missingBackends.Last();
                return missingBackends.Count == 1
                    ? $"The {last} backend's tool chain is not available."
                    : $"The {string.Join(", ", missingBackends.Take(missingBackends.Count - 1))} and {last} backends' tool chains are not available.";
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BackendTheoryAttribute"/> class.
        /// </summary>
        /// <param name="backends">The backends required.</param>
        public BackendTheoryAttribute(params Type[] backends) => Backends = backends;
    }
}