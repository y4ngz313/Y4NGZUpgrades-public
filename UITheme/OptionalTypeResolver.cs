using System;

namespace Y4NGZUpgrades.UITheme
{
    /// <summary>
    /// Resolves an ordered set of optional reflection providers once per initialization lifetime.
    /// A missing provider is a valid cached result because BepInEx finishes loading plugins before
    /// Y4NGZUpgrades initializes its UI bridges.
    /// </summary>
    internal sealed class OptionalTypeResolver
    {
        private readonly string[] _typeNames;
        private readonly Func<string, Type> _resolveType;
        private bool _resolved;
        private Type _resolvedType;

        internal OptionalTypeResolver(string[] typeNames, Func<string, Type> resolveType)
        {
            _typeNames = (string[])(typeNames ?? throw new ArgumentNullException(nameof(typeNames))).Clone();
            _resolveType = resolveType ?? throw new ArgumentNullException(nameof(resolveType));
        }

        internal Type Resolve()
        {
            if (_resolved)
                return _resolvedType;

            _resolved = true;
            for (int i = 0; i < _typeNames.Length && _resolvedType == null; i++)
                _resolvedType = _resolveType(_typeNames[i]);

            return _resolvedType;
        }

        internal void Reset()
        {
            _resolvedType = null;
            _resolved = false;
        }
    }
}
