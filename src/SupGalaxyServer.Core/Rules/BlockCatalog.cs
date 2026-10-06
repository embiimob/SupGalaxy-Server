namespace SupGalaxyServer.Rules;

/// <summary>One entry of SupGalaxy's BLOCKS table (js/declare.js), reduced to the fields the game rules use.</summary>
public sealed record BlockDef(
    int Id,
    string Name,
    double? Strength,
    bool Unbreakable = false,
    bool RequiresPick = false,
    bool Pickaxe = false,
    double? MeleeMultiplier = null,
    int? DropId = null,
    string? Model = null,
    int? OpenId = null,
    int? ClosedId = null,
    bool ItemOnly = false,
    bool Light = false);

/// <summary>
/// SupGalaxy's BLOCKS table and the rule helpers that depend on it (getMiningDamage, getPickaxeMultiplier).
/// Keep in sync with js/declare.js when blocks are added to the game.
/// </summary>
public static class BlockCatalog
{
    public const int Air = 0;
    public const int Bedrock = 1;
    public const int Water = 6;
    public const int Leaves = 8;
    public const int Lava = 16;
    public const int Obsidian = 110;
    public const int MagicianStone = 127;
    public const int CalligraphyStone = 128;
    public const int TreeSeed = 135;
    public const int Seaweed = 136;
    public const int IronPick = 174;
    public const int BlueIronPick = 175;

    private static readonly Dictionary<int, BlockDef> Blocks = new BlockDef[]
    {
        B(1, "Bedrock", 5, Unbreakable: true),
        B(2, "Grass", 1),
        B(3, "Dirt", 2),
        B(4, "Stone", 4, RequiresPick: true),
        B(5, "Sand", 2),
        B(6, "Water", 1),
        B(7, "Wood", 4),
        B(8, "Leaves", 1),
        B(9, "Cactus", 1),
        B(10, "Snow", 1),
        B(11, "Coal", 2),
        B(12, "Flower", 1),
        B(13, "Clay", 1),
        B(14, "Moss", 1),
        B(15, "Gravel", 1),
        B(16, "Lava", 1),
        B(17, "Ice", 1),
        B(100, "Glass", 2),
        B(101, "Stained Glass - Red", 2),
        B(102, "Stained Glass - Blue", 2),
        B(103, "Stained Glass - Green", 2),
        B(104, "Stained Glass - Yellow", 2),
        B(105, "Brick", 2),
        B(106, "Smooth Stone", 2),
        B(107, "Concrete", 3),
        B(108, "Polished Wood", 2),
        B(109, "Marble", 2),
        B(110, "Obsidian", 5),
        B(111, "Crystal - Blue", 1),
        B(112, "Crystal - Purple", 1),
        B(113, "Crystal - Green", 1),
        B(114, "Light Block", 1),
        B(115, "Glow Brick", 1),
        B(116, "Dark Glass", 2, RequiresPick: true),
        B(117, "Glass Tile", 2),
        B(118, "Sandstone", 1),
        B(119, "Cobblestone", 2),
        B(120, "Torch", 1, Light: true),
        B(121, "Laser Gun", 1),
        B(122, "Honey", 1),
        B(123, "Hive", 2),
        B(124, "Iron Ore", 4),
        B(125, "Emerald", 6, RequiresPick: true),
        B(126, "Green Laser Gun", 1),
        B(127, "Magician's Stone", 3),
        B(128, "Calligraphy Stone", 3),
        B(129, "Wooden Planks", 2),
        B(130, "Crafting Table", 2),
        B(131, "Chest", 2),
        B(133, "Blue Laser Gun", 1),
        B(134, "Blue Calcite", 4, Light: true),
        B(135, "Tree Seed", null),
        B(136, "Seaweed", 0.5),
        B(137, "Tuna", null, ItemOnly: true),
        B(138, "Iwashi", 1, ItemOnly: true),
        B(139, "Castle Stone Bricks", 30),
        B(140, "Mossy Castle Bricks", 30),
        B(141, "Chiseled Limestone", 2),
        B(142, "Polished Limestone", 2),
        B(143, "Red Roof Tile", 2),
        B(144, "Slate Roof Tile", 3),
        B(145, "Oak Support Beam", 3),
        B(146, "Oak Door", 2, Model: "door_closed", OpenId: 147),
        B(147, "Oak Door (Open)", 2, Model: "door_open", ClosedId: 146),
        B(148, "Oak Stairs", 2, Model: "stairs"),
        B(149, "Castle Stone Stairs", 30, Model: "stairs"),
        B(150, "Portcullis", 5, DropId: 151, Model: "portcullis"),
        B(151, "Portcullis", 5, Model: "portcullis"),
        B(152, "Rose Stained Glass", 2),
        B(153, "Battlement Stone", 30, Model: "battlement"),
        B(154, "Oak Door", 2, Model: "door_closed", OpenId: 155),
        B(155, "Oak Door (Open)", 2, Model: "door_open", ClosedId: 154),
        B(156, "Oak Door", 2, Model: "door_closed", OpenId: 157),
        B(157, "Oak Door (Open)", 2, Model: "door_open", ClosedId: 156),
        B(158, "Oak Door", 2, Model: "door_closed", OpenId: 159),
        B(159, "Oak Door (Open)", 2, Model: "door_open", ClosedId: 158),
        B(160, "Oak Stairs", 2, Model: "stairs"),
        B(161, "Oak Stairs", 2, Model: "stairs"),
        B(162, "Oak Stairs", 2, Model: "stairs"),
        B(163, "Castle Stone Stairs", 30, Model: "stairs"),
        B(164, "Castle Stone Stairs", 30, Model: "stairs"),
        B(165, "Castle Stone Stairs", 30, Model: "stairs"),
        B(166, "Wooden Planks", 2, DropId: 129),
        B(167, "Wooden Planks", 2, DropId: 129),
        B(168, "Wooden Planks", 2, DropId: 129),
        B(169, "Portcullis", 5, DropId: 151, Model: "portcullis"),
        B(170, "Portcullis", 5, DropId: 151, Model: "portcullis"),
        B(171, "Portcullis", 5, DropId: 151, Model: "portcullis"),
        B(172, "Polished Brick", 4),
        B(173, "Brick", 2, DropId: 105),
        B(174, "Iron Pick", null, Pickaxe: true, MeleeMultiplier: 2, ItemOnly: true),
        B(175, "Blue Iron Pick", null, Pickaxe: true, MeleeMultiplier: 3, ItemOnly: true),
        B(176, "Bone", null, ItemOnly: true),
    }.ToDictionary(b => b.Id);

    private static BlockDef B(int id, string name, double? strength, bool Unbreakable = false, bool RequiresPick = false,
        bool Pickaxe = false, double? MeleeMultiplier = null, int? DropId = null, string? Model = null, int? OpenId = null,
        int? ClosedId = null, bool ItemOnly = false, bool Light = false) =>
        new(id, name, strength, Unbreakable, RequiresPick, Pickaxe, MeleeMultiplier, DropId, Model, OpenId, ClosedId, ItemOnly, Light);

    public static BlockDef? Get(long id) => id is >= int.MinValue and <= int.MaxValue && Blocks.TryGetValue((int)id, out var b) ? b : null;

    public static string NameOf(long id) => Get(id)?.Name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Port of getPickaxeMultiplier: melee damage multiplier of the held tool.</summary>
    public static double PickaxeMultiplier(long? toolId) =>
        toolId is { } t && Get(t) is { Pickaxe: true } tool ? tool.MeleeMultiplier ?? 1 : 1;

    /// <summary>Port of getMiningDamage(blockId, toolId, laserColor). 0 means the block cannot be broken.</summary>
    public static double MiningDamage(long blockId, long? toolId, string? laserColor)
    {
        var block = Get(blockId);
        if (block == null || block.Unbreakable || blockId == Water) return 0;
        var strength = block.Strength ?? double.NaN;
        if (laserColor == "blue") return blockId == Obsidian ? strength / 4 : strength;
        if (laserColor == "green") return blockId == Obsidian ? 0 : 1;
        if (laserColor == "red") toolId = null;
        if (blockId == Obsidian) return toolId == BlueIronPick ? strength / 4 : 0;
        var tool = toolId is { } t ? Get(t) : null;
        if (block.RequiresPick && !(tool is { Pickaxe: true })) return 0;
        if (toolId == IronPick) return 2;
        if (toolId == BlueIronPick) return 4;
        return 1;
    }

    /// <summary>Hits needed to break a block (removeBlockAt: strength, defaulting to 1).</summary>
    public static double EffectiveStrength(long blockId)
    {
        var s = Get(blockId)?.Strength;
        return s is { } v && double.IsFinite(v) && v > 0 ? v : 1;
    }

    /// <summary>Blocks a new block may be placed into.</summary>
    public static bool IsReplaceable(long blockId) => blockId is Air or Water or Seaweed;
}
