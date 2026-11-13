using System.Reflection;
using System.Reflection.Emit;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models.Enums;

namespace FortniteHelper.Replays;

/// <summary>
/// The replicated state every searchable container shares (chests, ammo boxes, food boxes ...).
/// <c>SearchingPawn</c> is the pawn that opened it - by searching or by breaking it.
/// </summary>
public abstract class SearchableContainer : INetFieldExportGroup
{
    [NetFieldExport("bAlreadySearched", RepLayoutCmdType.PropertyBool)]
    public bool? bAlreadySearched { get; set; }

    [NetFieldExport("SearchingPawn", RepLayoutCmdType.PropertyObject)]
    public uint? SearchingPawn { get; set; }

    [NetFieldExport("bDestroyed", RepLayoutCmdType.PropertyBool)]
    public bool? bDestroyed { get; set; }

    [NetFieldExport("bHidden", RepLayoutCmdType.PropertyBool)]
    public bool? bHidden { get; set; }

    /// <summary>The container's class path (set by the generated subclasses).</summary>
    public abstract string ClassPath { get; }
}

/// <summary>
/// Container blueprints change every season, so a class is generated at runtime for each one
/// (Unreal.Core maps net field groups to classes by path). New ones are discovered from replays.
/// </summary>
public static class ContainerTypes
{
    public static readonly string[] Known =
    [
        "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C",
        "/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_NormalChest.AlwaysSpawn_NormalChest_C",
        "/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_RareChest.AlwaysSpawn_RareChest_C",
        "/RelicChest/Gameplay/B_TieredChest_Relics.B_TieredChest_Relics_C",
        "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena_FactionChest_NoLocks.Tiered_Chest_Athena_FactionChest_NoLocks_C",
        "/Game/Building/ActorBlueprints/Containers/Tiered_Ammo_Athena.Tiered_Ammo_Athena_C",
        "/Game/Building/ActorBlueprints/Containers/Tiered_Short_Ammo_3_Parent.Tiered_Short_Ammo_3_Parent_C",
        "/Game/Building/ActorBlueprints/Containers/FoodBox_Produce_Athena.FoodBox_Produce_Athena_C",
        "/Game/Building/ActorBlueprints/Containers/Barrel_FishingRod_Container_Athena.Barrel_FishingRod_Container_Athena_C",
    ];

    static readonly object Lock = new();
    static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);
    static ModuleBuilder? _module;
    static int _counter;

    static ContainerTypes() => Register(Known);

    /// <summary>Makes sure the generated classes exist before a reader is created (the parser scans types on construction).</summary>
    public static void EnsureInitialized() { lock (Lock) { } }

    public static bool IsRegistered(string classPath)
    {
        lock (Lock) return Registered.Contains(classPath);
    }

    /// <summary>Generates a net field export class for each container path not registered yet. Returns how many were new.</summary>
    public static int Register(IEnumerable<string> classPaths)
    {
        int added = 0;
        lock (Lock)
        {
            foreach (var path in classPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !Registered.Add(path)) continue;
                _module ??= AssemblyBuilder
                    .DefineDynamicAssembly(new AssemblyName("FortniteHelper.Dynamic.ReplayReader"), AssemblyBuilderAccess.Run)
                    .DefineDynamicModule("Containers");

                var tb = _module.DefineType($"FortniteHelper.Replays.Generated.Container{++_counter}",
                    TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(SearchableContainer));
                var attrCtor = typeof(NetFieldExportGroupAttribute).GetConstructor([typeof(string), typeof(ParseMode)])!;
                tb.SetCustomAttribute(new CustomAttributeBuilder(attrCtor, [path, ParseMode.Minimal]));
                tb.DefineDefaultConstructor(MethodAttributes.Public);

                // override string ClassPath => path;
                var getter = tb.DefineMethod("get_ClassPath",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                    typeof(string), Type.EmptyTypes);
                var il = getter.GetILGenerator();
                il.Emit(OpCodes.Ldstr, path);
                il.Emit(OpCodes.Ret);
                tb.DefineMethodOverride(getter, typeof(SearchableContainer).GetProperty(nameof(SearchableContainer.ClassPath))!.GetMethod!);

                tb.CreateType();
                added++;
            }
        }
        return added;
    }

    public static string ShortName(string classPath)
    {
        var s = classPath;
        int dot = s.LastIndexOf('.');
        if (dot >= 0) s = s[(dot + 1)..];
        if (s.EndsWith("_C", StringComparison.Ordinal)) s = s[..^2];
        return s;
    }

    public static ContainerType TypeOf(string classPath)
    {
        var n = ShortName(classPath);
        if (n.Contains("Ammo", StringComparison.OrdinalIgnoreCase)) return ContainerType.AmmoBox;
        if (n.Contains("Chest", StringComparison.OrdinalIgnoreCase)) return ContainerType.Chest;
        return ContainerType.Other;
    }

    public static bool IsRare(string classPath) => ShortName(classPath).Contains("Rare", StringComparison.OrdinalIgnoreCase);
}
