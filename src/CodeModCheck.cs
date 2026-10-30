using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Ostrasort;

/// <summary>One plugin DLL that uses game code the installed build no longer has.</summary>
public sealed record CodeModIssue(string PluginPath, string Owner, IReadOnlyList<string> Missing);

/// <summary>
/// Checks BepInEx plugins against the installed game build. A game patch that
/// renames or re-signs a method breaks every plugin compiled against the old
/// one with MissingMethodException at runtime, and the player only finds out
/// when the game crashes. Each plugin's metadata already lists every type,
/// method and field it uses from Assembly-CSharp, so comparing those against
/// the installed Assembly-CSharp finds the breakage before launch.
///
/// Reads metadata only (System.Reflection.Metadata): nothing is loaded or run.
/// Also checks [HarmonyPatch] attribute targets, since a patch whose target
/// method is gone fails when the plugin starts. Invisible to this: string
/// lookups such as AccessTools.Method("Name") and MonoMod patches.
///
/// Conservative by design. A member is reported missing only when the game
/// type exists and neither it nor any base class declares it. Base classes are
/// followed into the other assemblies in the Managed folder; when one cannot be
/// read the search gives up instead of flagging.
/// </summary>
public static class CodeModCheck
{
    public const string GameAssemblyName = "Assembly-CSharp";

    public static string GameAssemblyPath(GameEnv env) =>
        Path.Combine(env.GameRoot, "Ostranauts_Data", "Managed", GameAssemblyName + ".dll");

    /// <summary>
    /// Every plugin DLL under BepInEx\plugins with what it is missing. Plugins
    /// that use nothing from the game, or nothing that has gone, are left out.
    /// Owner is the folder the player recognises: the top-level folder under
    /// plugins, or the Workshop item id for bridge-managed copies.
    /// </summary>
    public static List<CodeModIssue> CheckInstall(GameEnv env)
    {
        var game = GameAssemblyPath(env);
        var plugins = Path.Combine(env.CodeBepInExDir, "plugins");
        if (!File.Exists(game) || !Directory.Exists(plugins)) return new();

        List<string> dlls;
        try { dlls = Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }

        using var index = GameIndex.Open(game);
        var issues = new List<CodeModIssue>();
        foreach (var dll in dlls.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var missing = Check(index, dll);
            if (missing.Count > 0) issues.Add(new CodeModIssue(dll, OwnerOf(plugins, dll), missing));
        }
        return issues;
    }

    /// <summary>What one plugin uses that the game no longer has. Empty when unreadable or clean.</summary>
    public static List<string> Check(string gameAssembly, string pluginDll)
    {
        using var index = GameIndex.Open(gameAssembly);
        return Check(index, pluginDll);
    }

    /// <summary>"Workshop 123", the top-level folder under plugins, or the DLL name for a loose file.</summary>
    public static string OwnerOf(string pluginsRoot, string dll)
    {
        var parts = Path.GetRelativePath(pluginsRoot, dll).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length >= 3 && parts[0].Equals("Workshop", StringComparison.OrdinalIgnoreCase)) return "Workshop " + parts[1];
        return parts.Length >= 2 ? parts[0] : Path.GetFileNameWithoutExtension(dll);
    }

    private static List<string> Check(GameIndex game, string pluginDll)
    {
        var missing = new List<string>();
        try
        {
            using var fs = File.OpenRead(pluginDll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return missing;
            var md = pe.GetMetadataReader();
            if (!ReferencesGame(md)) return missing;

            var names = new SigNames(md);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string s) { if (seen.Add(s)) missing.Add(s); }

            foreach (var h in md.TypeReferences)
            {
                if (!IsGameType(md, h)) continue;
                var name = names.RefName(h);
                if (!game.HasType(name)) Add($"type {Pretty(name)}");
            }

            foreach (var h in md.MemberReferences)
            {
                var mr = md.GetMemberReference(h);
                var owner = ParentType(md, names, mr.Parent);
                if (owner is null || !game.HasType(owner)) continue;   // not game code, or already reported as a type
                var member = md.GetString(mr.Name);
                if (mr.GetKind() == MemberReferenceKind.Method)
                {
                    var sig = Describe(mr.DecodeMethodSignature(names, null));
                    if (game.FindMethod(owner, member, sig) == false) Add($"method {Pretty(owner)}.{member}");
                }
                else if (mr.GetKind() == MemberReferenceKind.Field)
                {
                    if (game.FindField(owner, member) == false) Add($"field {Pretty(owner)}.{member}");
                }
            }

            foreach (var target in HarmonyTargets(md, names))
            {
                if (!game.HasType(target.Type))
                {
                    Add($"type {Pretty(target.Type)}");
                    continue;
                }
                if (target.Method is { } m && game.FindMethodByName(target.Type, m) == false)
                    Add($"patch target {Pretty(target.Type)}.{m.Replace("get_", "").Replace("set_", "")}");
            }
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // not a managed assembly, or locked: nothing to say about it
        }
        return missing;
    }

    private static string Pretty(string typeName) => typeName.Replace('/', '.');

    private static bool ReferencesGame(MetadataReader md) =>
        md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) == GameAssemblyName);

    private static bool IsGameType(MetadataReader md, TypeReferenceHandle h)
    {
        var scope = md.GetTypeReference(h).ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference) scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        return scope.Kind == HandleKind.AssemblyReference
            && md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) == GameAssemblyName;
    }

    /// <summary>The game type a member reference belongs to, or null when it is not a game type.</summary>
    private static string? ParentType(MetadataReader md, SigNames names, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                var tr = (TypeReferenceHandle)parent;
                return IsGameType(md, tr) ? names.RefName(tr) : null;
            case HandleKind.TypeSpecification:
                // a generic game type instantiated by the plugin: Foo`1<int> -> Foo`1
                var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                blob.ReadCompressedInteger();   // CLASS or VALUETYPE
                var gen = blob.ReadTypeHandle();
                return gen.Kind == HandleKind.TypeReference && IsGameType(md, (TypeReferenceHandle)gen)
                    ? names.RefName((TypeReferenceHandle)gen) : null;
            default:
                return null;
        }
    }

    /// <summary>A method signature as a comparable string, the same shape on both sides.</summary>
    internal static string Describe(MethodSignature<string> sig) =>
        $"{(sig.Header.IsInstance ? "" : "static ")}`{sig.GenericParameterCount} {sig.ReturnType}({string.Join(",", sig.ParameterTypes)})";

    // ------------------------------------------------------------ Harmony ---

    private sealed record HarmonyTarget(string Type, string? Method);

    private sealed record PatchInfo(string? Type, string? Method, int MethodType)
    {
        public PatchInfo Merge(PatchInfo inner) =>
            new(inner.Type ?? Type, inner.Method ?? Method, inner.MethodType != 0 ? inner.MethodType : MethodType);
    }

    /// <summary>
    /// The game methods named by [HarmonyPatch] attributes. Harmony merges a
    /// class-level attribute with the attributes on its patch methods, so a
    /// class with method-level attributes yields one target per method.
    /// </summary>
    private static IEnumerable<HarmonyTarget> HarmonyTargets(MetadataReader md, SigNames names)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            var outer = Merge(md, td.GetCustomAttributes());
            if (outer is null) continue;

            var inner = td.GetMethods()
                .Select(mh => Merge(md, md.GetMethodDefinition(mh).GetCustomAttributes()))
                .Where(p => p is not null).Select(p => outer.Merge(p!)).ToList();
            foreach (var p in inner.Count > 0 ? inner : new List<PatchInfo> { outer })
                if (Resolve(p) is { } t) yield return t;
        }
    }

    private static HarmonyTarget? Resolve(PatchInfo p)
    {
        if (p.Type is null) return null;
        // only game types are checked; a serialized type name carries its assembly
        var comma = p.Type.IndexOf(',');
        if (comma < 0) return null;
        var asm = p.Type[(comma + 1)..].Split(',')[0].Trim();
        if (asm != GameAssemblyName) return null;
        var type = p.Type[..comma].Trim().Replace('+', '/');
        var method = p.MethodType switch
        {
            1 => p.Method is null ? null : "get_" + p.Method,   // MethodType.Getter
            2 => p.Method is null ? null : "set_" + p.Method,   // MethodType.Setter
            3 => ".ctor",                                       // MethodType.Constructor
            4 => ".cctor",                                      // MethodType.StaticConstructor
            _ => p.Method,
        };
        return new HarmonyTarget(type, method);
    }

    /// <summary>Folds every [HarmonyPatch] on one member into one partial target, or null when there is none.</summary>
    private static PatchInfo? Merge(MetadataReader md, CustomAttributeHandleCollection attrs)
    {
        PatchInfo? acc = null;
        foreach (var ah in attrs)
        {
            var ca = md.GetCustomAttribute(ah);
            if (!IsHarmonyPatch(md, ca)) continue;
            CustomAttributeValue<string> value;
            try { value = ca.DecodeValue(AttrTypes.Instance); }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or NotSupportedException) { continue; }

            string? type = null, method = null;
            var methodType = 0;
            var strings = new List<string>();
            foreach (var arg in value.FixedArguments)
            {
                if (arg.Type == "System.Type" && arg.Value is string t) type ??= t;
                else if (arg.Type == "System.String" && arg.Value is string s) strings.Add(s);
                else if (arg.Type == "HarmonyLib.MethodType" && arg.Value is int mt) methodType = mt;
            }
            // HarmonyPatch(string typeName, string methodName) names the type as a string
            if (type is null && strings.Count >= 2) { type = strings[0]; method = strings[1]; }
            else if (strings.Count >= 1) method = strings[0];

            var info = new PatchInfo(type, method, methodType);
            acc = acc is null ? info : acc.Merge(info);
        }
        return acc;
    }

    private static bool IsHarmonyPatch(MetadataReader md, CustomAttribute ca)
    {
        EntityHandle typeHandle;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            typeHandle = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
        else return false;   // a HarmonyPatch defined in the plugin itself is not Harmony's
        if (typeHandle.Kind != HandleKind.TypeReference) return false;
        var tr = md.GetTypeReference((TypeReferenceHandle)typeHandle);
        return md.GetString(tr.Namespace) == "HarmonyLib" && md.GetString(tr.Name) == "HarmonyPatch";
    }

    // ------------------------------------------------- signature decoding ---

    /// <summary>Type names for signatures: Namespace.Name, nested as Outer/Inner, assembly left out.</summary>
    internal sealed class SigNames : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _md;
        public SigNames(MetadataReader md) => _md = md;

        public string RefName(TypeReferenceHandle h)
        {
            var tr = _md.GetTypeReference(h);
            var name = _md.GetString(tr.Name);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                return RefName((TypeReferenceHandle)tr.ResolutionScope) + "/" + name;
            var ns = _md.GetString(tr.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string DefName(TypeDefinitionHandle h)
        {
            var td = _md.GetTypeDefinition(h);
            var name = _md.GetString(td.Name);
            var outer = td.GetDeclaringType();
            if (!outer.IsNil) return DefName(outer) + "/" + name;
            var ns = _md.GetString(td.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => DefName(handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => RefName(handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPinnedType(string elementType) => elementType;
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }

    /// <summary>Just enough of a type provider to read [HarmonyPatch] arguments.</summary>
    private sealed class AttrTypes : ICustomAttributeTypeProvider<string>
    {
        public static readonly AttrTypes Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            return reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            return reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
        }
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;   // every Harmony enum is int-backed
        public bool IsSystemType(string type) => type == "System.Type";
    }

    // --------------------------------------------------------- game index ---

    /// <summary>
    /// The installed Assembly-CSharp, indexed by type name, signatures decoded on
    /// demand. Base classes outside it (MonoBehaviour, System.Object) are followed
    /// into the sibling assemblies in the same Managed folder, so a member gone
    /// from a game type is still caught when the type derives from a Unity class.
    /// The search gives up (unknown) only when a base assembly cannot be read.
    /// </summary>
    private sealed class GameIndex : IDisposable
    {
        private sealed class Module
        {
            public required PEReader Pe { get; init; }
            public required MetadataReader Md { get; init; }
            public required SigNames Names { get; init; }
            public Dictionary<string, TypeDefinitionHandle> Types { get; } = new(StringComparer.Ordinal);
            public Dictionary<TypeDefinitionHandle, Dictionary<string, List<string>>> Methods { get; } = new();
        }

        private readonly string _dir;
        private readonly Module _game;
        private readonly Dictionary<string, Module?> _others = new(StringComparer.OrdinalIgnoreCase);

        private GameIndex(string path)
        {
            _dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            _game = Load(path) ?? throw new BadImageFormatException($"{path} has no metadata");
        }

        public static GameIndex Open(string path) => new(path);

        public void Dispose()
        {
            _game.Pe.Dispose();
            foreach (var m in _others.Values) m?.Pe.Dispose();
        }

        private static Module? Load(string path)
        {
            var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata) { pe.Dispose(); return null; }
            var md = pe.GetMetadataReader();
            var mod = new Module { Pe = pe, Md = md, Names = new SigNames(md) };
            foreach (var h in md.TypeDefinitions) mod.Types[mod.Names.DefName(h)] = h;
            return mod;
        }

        private Module? Assembly(string name)
        {
            if (_others.TryGetValue(name, out var mod)) return mod;
            var path = Path.Combine(_dir, name + ".dll");
            try { mod = File.Exists(path) ? Load(path) : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { mod = null; }
            _others[name] = mod;
            return mod;
        }

        public bool HasType(string name) => _game.Types.ContainsKey(name);

        /// <summary>True found, false definitely missing, null unknown (a base class could not be read).</summary>
        public bool? FindMethod(string type, string name, string sig) =>
            Walk(type, (m, h) => Methods(m, h).TryGetValue(name, out var sigs) && sigs.Contains(sig));

        public bool? FindMethodByName(string type, string name) =>
            Walk(type, (m, h) => Methods(m, h).ContainsKey(name)
                              || m.Md.GetTypeDefinition(h).GetProperties().Any(p => m.Md.GetString(m.Md.GetPropertyDefinition(p).Name) == name));

        public bool? FindField(string type, string name) =>
            Walk(type, (m, h) => m.Md.GetTypeDefinition(h).GetFields().Any(f => m.Md.GetString(m.Md.GetFieldDefinition(f).Name) == name));

        private bool? Walk(string type, Func<Module, TypeDefinitionHandle, bool> declares)
        {
            if (!_game.Types.TryGetValue(type, out var h)) return null;
            var mod = _game;
            for (var depth = 0; depth < 64; depth++)
            {
                if (declares(mod, h)) return true;
                var td = mod.Md.GetTypeDefinition(h);
                if ((td.Attributes & System.Reflection.TypeAttributes.Interface) != 0) return null;   // interface members come from elsewhere
                var baseType = td.BaseType;
                if (baseType.IsNil) return false;
                if (baseType.Kind == HandleKind.TypeSpecification)
                {
                    // a generic base: Foo<int> -> Foo`1
                    var blob = mod.Md.GetBlobReader(mod.Md.GetTypeSpecification((TypeSpecificationHandle)baseType).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                    blob.ReadCompressedInteger();
                    baseType = blob.ReadTypeHandle();
                }
                switch (baseType.Kind)
                {
                    case HandleKind.TypeDefinition:
                        h = (TypeDefinitionHandle)baseType;
                        break;
                    case HandleKind.TypeReference:
                        if (Resolve(mod, (TypeReferenceHandle)baseType) is not { } next)
                            return IsCoreRoot(mod, (TypeReferenceHandle)baseType) ? false : null;
                        (mod, h) = next;
                        break;
                    default:
                        return null;
                }
            }
            return null;
        }

        /// <summary>
        /// System.Object and the value-type roots declare nothing a plugin reaches
        /// through a game type (the compiler names Object itself for ToString and
        /// friends), so reaching one unread still means the member is gone.
        /// </summary>
        private static bool IsCoreRoot(Module mod, TypeReferenceHandle h)
        {
            var tr = mod.Md.GetTypeReference(h);
            return mod.Md.GetString(tr.Namespace) == "System" && mod.Md.GetString(tr.Name) is "Object" or "ValueType" or "Enum";
        }

        /// <summary>A type reference followed to its definition in a sibling assembly, or null.</summary>
        private (Module, TypeDefinitionHandle)? Resolve(Module from, TypeReferenceHandle tr)
        {
            var scope = from.Md.GetTypeReference(tr).ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference) scope = from.Md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            if (scope.Kind != HandleKind.AssemblyReference) return null;
            var asm = from.Md.GetString(from.Md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            var name = from.Names.RefName(tr);
            // type forwarding (netstandard -> mscorlib) is not followed; mscorlib holds the core types
            foreach (var candidate in new[] { asm, "mscorlib" })
                if (Assembly(candidate) is { } target && target.Types.TryGetValue(name, out var h))
                    return (target, h);
            return null;
        }

        private static Dictionary<string, List<string>> Methods(Module mod, TypeDefinitionHandle h)
        {
            if (mod.Methods.TryGetValue(h, out var byName)) return byName;
            byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var mh in mod.Md.GetTypeDefinition(h).GetMethods())
            {
                var m = mod.Md.GetMethodDefinition(mh);
                var name = mod.Md.GetString(m.Name);
                if (!byName.TryGetValue(name, out var list)) byName[name] = list = new();
                try { list.Add(Describe(m.DecodeSignature(mod.Names, null))); }
                catch (BadImageFormatException) { list.Add("?"); }
            }
            mod.Methods[h] = byName;
            return byName;
        }
    }
}