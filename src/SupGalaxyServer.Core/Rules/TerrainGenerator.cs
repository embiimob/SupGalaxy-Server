using System.Globalization;

namespace SupGalaxyServer.Rules;

/// <summary>World archetype (js/worker.js ARCHETYPES), selected deterministically from the world seed.</summary>
public sealed record WorldArchetype(
    string Name,
    string Generator,
    bool NoWater = false,
    bool OnlyDesert = false,
    bool LargeBiomes = false,
    bool Trees = false,
    bool Flowers = false,
    bool Hives = false,
    bool Cactus = false);

/// <summary>A volcano caldera found while generating a chunk (same detection as generateVulcanTerrain).</summary>
public sealed record VolcanoInfo(double X, double Y, double Z, int LavaCount, string ChunkKey);

/// <summary>Raw terrain of one chunk: 16 x 256 x 16 block ids indexed like SupGalaxy's Chunk.idx.</summary>
public sealed class TerrainChunk
{
    public TerrainChunk(string key, byte[] data, WorldArchetype archetype, VolcanoInfo? volcano)
    {
        Key = key;
        Data = data;
        Archetype = archetype;
        Volcano = volcano;
    }

    public string Key { get; }
    public byte[] Data { get; }
    public WorldArchetype Archetype { get; }
    public VolcanoInfo? Volcano { get; }

    public int Get(int lx, int y, int lz) =>
        lx < 0 || lx >= TerrainGenerator.ChunkSize || lz < 0 || lz >= TerrainGenerator.ChunkSize || y < 0 || y >= TerrainGenerator.MaxHeight
            ? BlockCatalog.Air
            : Data[TerrainGenerator.Index(lx, y, lz)];
}

/// <summary>
/// C# port of SupGalaxy's procedural terrain (js/worker.js: selectArchetype, generateStandardTerrain,
/// generateMoonTerrain, generateVulcanTerrain, generateDesertTerrain, addSeaweedPatches, placeTree, ...).
///
/// The output is bit-identical to the browser for everything the browser computes deterministically. Two spots in
/// the original use Math.random() (bee hive height and Vulcan blue-calcite stalactites), so every browser already
/// sees something different there; the server uses a seeded stream (chunkKey + "_server_random") instead.
/// </summary>
public static class TerrainGenerator
{
    public const int ChunkSize = 16;
    public const int MaxHeight = 256;
    public const int SeaLevel = 16;
    public const int MapSize = 16384;
    public const int ChunksPerAxis = MapSize / ChunkSize;
    public const int ChunkVolume = ChunkSize * MaxHeight * ChunkSize;

    public static int Index(int lx, int y, int lz) => (y * ChunkSize + lz) * ChunkSize + lx;

    // Object.keys(ARCHETYPES) order matters for selectArchetype.
    public static readonly WorldArchetype[] Archetypes =
    {
        new("Earth", "generateStandardTerrain", Trees: true, Flowers: true, Hives: true),
        new("Moon", "generateMoonTerrain", NoWater: true),
        new("Vulcan", "generateVulcanTerrain"),
        new("Desert", "generateDesertTerrain", OnlyDesert: true, Cactus: true),
        new("Massive", "generateStandardTerrain", LargeBiomes: true, Trees: true, Flowers: true, Hives: true),
    };

    private sealed record Biome(string Key, int[] Palette, double HeightScale, double Roughness, double FeatureDensity);

    private static readonly Biome[] Biomes =
    {
        new("plains", new[] { 2, 3, 4, 13, 15 }, 0.8, 0.3, 0.05),
        new("desert", new[] { 5, 118, 4 }, 0.6, 0.4, 0.02),
        new("forest", new[] { 2, 3, 14, 4 }, 1.3, 0.4, 0.03),
        new("snow", new[] { 10, 17, 4 }, 1.2, 0.5, 0.02),
        new("mountain", new[] { 4, 11, 3, 15, 1 }, 10.5, 0.6, 0.01),
        new("swamp", new[] { 2, 3, 6, 14, 13 }, 0.5, 0.2, 0.04),
    };

    /// <summary>The terrain seed is the first ':' separated part of the chunk key, i.e. makeChunkKey's 8-char prefix.</summary>
    public static string SeedOf(string world) => MakeChunkKey(world, 0, 0).Split(':')[0];

    public static string MakeChunkKey(string world, long cx, long cz) =>
        (world.Length > 8 ? world[..8] : world) + ":" + cx.ToString(CultureInfo.InvariantCulture) + ":" + cz.ToString(CultureInfo.InvariantCulture);

    public static WorldArchetype SelectArchetype(string seed)
    {
        var rnd = JsRandom.MakeSeededRandom(seed + "_archetype_selector");
        return Archetypes[(int)Math.Floor(rnd.Next() * Archetypes.Length)];
    }

    /// <summary>Port of generateChunkData(chunkKey).</summary>
    public static TerrainChunk Generate(string chunkKey)
    {
        var parts = chunkKey.Split(':');
        var seed = parts[0];
        var archetype = SelectArchetype(seed);
        var data = new byte[ChunkVolume];
        VolcanoInfo? volcano = null;
        if (parts.Length < 3 || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cx)
            || !long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cz))
        {
            // parseInt would yield NaN here and the browser generates an empty chunk.
            return new TerrainChunk(chunkKey, data, archetype, null);
        }

        var serverRandom = JsRandom.MakeSeededRandom(chunkKey + "_server_random");
        switch (archetype.Generator)
        {
            case "generateMoonTerrain":
                GenerateMoon(data, seed, cx, cz);
                break;
            case "generateVulcanTerrain":
                volcano = GenerateVulcan(data, chunkKey, seed, cx, cz, serverRandom);
                break;
            default:
                GenerateStandard(data, chunkKey, seed, cx, cz, archetype, serverRandom);
                break;
        }
        return new TerrainChunk(chunkKey, data, archetype, volcano);
    }

    private static Biome PickBiome(double n, Biome[] biomes, WorldArchetype archetype)
    {
        Biome Find(string key, int fallback) => biomes.FirstOrDefault(b => b.Key == key) ?? biomes[fallback];
        if (archetype.OnlyDesert) return Find("desert", 1);
        if (n > 0.68) return Find("snow", 0);
        if (n < 0.25) return Find("desert", 1);
        if (n > 0.45) return Find("forest", 2);
        if (n > 0.60) return Find("mountain", 4);
        if (n < 0.35) return Find("swamp", 5);
        return Find("plains", 0);
    }

    private static void GenerateStandard(byte[] d, string chunkKey, string seed, long cx, long cz, WorldArchetype archetype, JsRandom.SeededRandom serverRandom)
    {
        var biomeRnd = JsRandom.MakeSeededRandom(seed + "_biomes");
        var biomes = Biomes.Select(b =>
        {
            var hs = Math.Max(0.1, b.HeightScale + (biomeRnd.Next() - 0.5) * b.HeightScale * 0.5);
            var r = Math.Max(0.1, b.Roughness + (biomeRnd.Next() - 0.5) * b.Roughness * 0.5);
            var f = Math.Max(0.005, b.FeatureDensity + (biomeRnd.Next() - 0.5) * b.FeatureDensity * 0.5);
            return b with { HeightScale = hs, Roughness = r, FeatureDensity = f };
        }).ToArray();
        var noise = JsRandom.MakeNoise(seed);
        var blockNoise = JsRandom.MakeNoise(seed + "_block");
        var chunkRnd = JsRandom.MakeSeededRandom(chunkKey);
        long baseX = cx * ChunkSize, baseZ = cz * ChunkSize;
        var hiveNoise = JsRandom.MakeNoise(seed + "_hive");

        for (int lx = 0; lx < ChunkSize; lx++)
        {
            for (int lz = 0; lz < ChunkSize; lz++)
            {
                long wx = baseX + lx, wz = baseZ + lz;
                double nx = (double)(wx % MapSize) / MapSize * 10000;
                double nz = (double)(wz % MapSize) / MapSize * 10000;
                double biomeNoiseScale = archetype.LargeBiomes ? 0.002 : 0.005;
                double n = JsRandom.Fbm(noise, nx * biomeNoiseScale, nz * biomeNoiseScale, 5, 0.6);
                var biome = PickBiome(n, biomes, archetype);
                double heightScale = biome.HeightScale, roughness = biome.Roughness;
                double height = Math.Floor(n * 40 * heightScale + 8);
                if (n > 0.7) height += Math.Floor((n - 0.7) * 60 * heightScale);
                double localN = JsRandom.Fbm(noise, nx * 0.05, nz * 0.05, 4, 0.5);
                height += Math.Floor(localN * 15 * roughness);
                int h = (int)Math.Max(1, Math.Min(MaxHeight - 1, height));
                for (int y = 0; y <= h; y++)
                {
                    int id;
                    if (y == 0) id = 1;
                    else if (y < h - 3) id = 4;
                    else if (y < h) id = 3;
                    else
                    {
                        double blockN = JsRandom.Fbm(blockNoise, nx * 0.1, nz * 0.1, 3, 0.6);
                        int paletteIndex = (int)Math.Floor(blockN * biome.Palette.Length);
                        id = biome.Palette[paletteIndex % biome.Palette.Length];
                    }
                    d[Index(lx, y, lz)] = (byte)id;
                }
                if (!archetype.NoWater)
                    for (int y = h + 1; y <= SeaLevel; y++) d[Index(lx, y, lz)] = BlockCatalog.Water;

                double hiveValue = hiveNoise.Sample(nx * 0.1, nz * 0.1);
                if (archetype.Hives && biome.Key == "forest" && hiveValue > 0.98)
                    PlaceHive(d, lx, h + 1, lz, serverRandom);
                else if (archetype.Trees && biome.Key == "forest" && chunkRnd.Next() < biome.FeatureDensity)
                    PlaceTree(d, lx, h + 1, lz, chunkRnd);
                else if (archetype.Flowers && biome.Key == "plains" && chunkRnd.Next() < biome.FeatureDensity)
                    PlaceFlower(d, lx, h + 1, lz);
                else if (archetype.Cactus && biome.Key == "desert" && chunkRnd.Next() < biome.FeatureDensity)
                    PlaceCactus(d, lx, h + 1, lz, chunkRnd);
            }
        }
        if (!archetype.NoWater) AddSeaweedPatches(d, seed, baseX, baseZ, SeaLevel, 20);
    }

    private static void AddSeaweedPatches(byte[] d, string seed, long baseX, long baseZ, int seaLevel, int maxDepth)
    {
        for (int lx = 0; lx < ChunkSize; lx++)
        {
            for (int lz = 0; lz < ChunkSize; lz++)
            {
                int floorY = seaLevel;
                while (floorY > 0 && d[Index(lx, floorY, lz)] == BlockCatalog.Water) floorY--;
                int depth = seaLevel - floorY;
                if (depth <= 10 || depth > maxDepth) continue;
                long wx = baseX + lx, wz = baseZ + lz;
                var patchRandom = JsRandom.MakeSeededRandom(seed + "_seaweed_" + wx.ToString(CultureInfo.InvariantCulture) + "_" + wz.ToString(CultureInfo.InvariantCulture));
                if (patchRandom.Next() > 0.006) continue;
                for (int dx = -2; dx <= 2; dx++)
                {
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        int px = lx + dx, pz = lz + dz;
                        if (px < 0 || px >= ChunkSize || pz < 0 || pz >= ChunkSize) continue;
                        if ((dx != 0 || dz != 0) && patchRandom.Next() > 0.35) continue;
                        int plantFloor = seaLevel;
                        while (plantFloor > 0 && d[Index(px, plantFloor, pz)] == BlockCatalog.Water) plantFloor--;
                        int plantDepth = seaLevel - plantFloor;
                        if (plantDepth <= 10 || plantDepth > maxDepth) continue;
                        int growth = 1 + (int)Math.Floor(patchRandom.Next() * 8);
                        for (int dy = 1; dy <= growth && plantFloor + dy <= seaLevel; dy++)
                        {
                            int index = Index(px, plantFloor + dy, pz);
                            if (d[index] != BlockCatalog.Water) break;
                            d[index] = BlockCatalog.Seaweed;
                        }
                    }
                }
            }
        }
    }

    private static void PlaceTree(byte[] d, int lx, int cy, int lz, JsRandom.SeededRandom rnd)
    {
        int treeHeight = 5 + (int)Math.Floor(rnd.Next() * 6);
        int canopySize = 2 + (int)Math.Floor(rnd.Next() * 2);
        for (int i = 0; i < treeHeight; i++)
            if (cy + i < MaxHeight) d[Index(lx, cy + i, lz)] = 7;
        for (int dy = -canopySize; dy <= canopySize; dy++)
        {
            for (int dx = -canopySize; dx <= canopySize; dx++)
            {
                for (int dz = -canopySize; dz <= canopySize; dz++)
                {
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist <= canopySize + 0.5 * rnd.Next())
                    {
                        int rx = lx + dx, ry = cy + treeHeight + dy, rz = lz + dz;
                        if (ry < MaxHeight && rx >= 0 && rx < ChunkSize && rz >= 0 && rz < ChunkSize && d[Index(rx, ry, rz)] == BlockCatalog.Air)
                            d[Index(rx, ry, rz)] = BlockCatalog.Leaves;
                    }
                }
            }
        }
    }

    private static void PlaceFlower(byte[] d, int lx, int cy, int lz)
    {
        if (cy < MaxHeight && d[Index(lx, cy, lz)] == BlockCatalog.Air) d[Index(lx, cy, lz)] = 12;
    }

    private static void PlaceCactus(byte[] d, int lx, int cy, int lz, JsRandom.SeededRandom rnd)
    {
        int h = 1 + (int)Math.Floor(rnd.Next() * 3);
        for (int i = 0; i < h; i++)
            if (cy + i < MaxHeight) d[Index(lx, cy + i, lz)] = 9;
    }

    private static void PlaceHive(byte[] d, int lx, int cy, int lz, JsRandom.SeededRandom random)
    {
        int hiveHeight = 2 + (int)Math.Floor(random.Next() * 2);
        for (int i = 0; i < hiveHeight; i++)
            if (cy + i < MaxHeight) d[Index(lx, cy + i, lz)] = 123;
    }

    private static void GenerateMoon(byte[] d, string seed, long cx, long cz)
    {
        var noise = JsRandom.MakeNoise(seed);
        var craterNoise = JsRandom.MakeNoise(seed + "_craters");
        long baseX = cx * ChunkSize, baseZ = cz * ChunkSize;
        for (int lx = 0; lx < ChunkSize; lx++)
        {
            for (int lz = 0; lz < ChunkSize; lz++)
            {
                long wx = baseX + lx, wz = baseZ + lz;
                double nx = (double)(wx % MapSize) / MapSize * 100;
                double nz = (double)(wz % MapSize) / MapSize * 100;
                double height = 30 + JsRandom.Fbm(noise, nx * 0.1, nz * 0.1, 6, 0.5) * 20;
                double craterValue = JsRandom.Fbm(craterNoise, nx * 0.5, nz * 0.5, 3, 0.5);
                if (craterValue > 0.7) height -= (craterValue - 0.7) * 30;
                height = Math.Max(1, Math.Min(MaxHeight - 1, height));
                for (int y = 0; y <= height; y++) d[Index(lx, y, lz)] = (byte)(y == 0 ? 1 : 4);
            }
        }
    }

    private static VolcanoInfo? GenerateVulcan(byte[] d, string chunkKey, string seed, long cx, long cz, JsRandom.SeededRandom serverRandom)
    {
        int vulcanSeaLevel = 32 + (int)Math.Floor(JsRandom.MakeSeededRandom(seed + "_vulcan_sea_level").Next() * 8);
        var noise = JsRandom.MakeNoise(seed);
        var mountainNoise = JsRandom.MakeNoise(seed + "_mountains");
        var resourceNoise = JsRandom.MakeNoise(seed + "_resources");
        var cavernNoise = JsRandom.MakeNoise(seed + "_caverns");
        var cavernYNoise = JsRandom.MakeNoise(seed + "_caverny");
        long baseX = cx * ChunkSize, baseZ = cz * ChunkSize;

        for (int lx = 0; lx < ChunkSize; lx++)
        {
            for (int lz = 0; lz < ChunkSize; lz++)
            {
                long wx = baseX + lx, wz = baseZ + lz;
                double nx = (double)(wx % MapSize) / MapSize * 200;
                double nz = (double)(wz % MapSize) / MapSize * 200;

                double mountainHeight = JsRandom.Fbm(mountainNoise, nx * 0.3, nz * 0.3, 8, 0.55);
                mountainHeight = Math.Pow(mountainHeight, 2.5) * 220;
                double cavernHeightRadius = JsRandom.Fbm(cavernNoise, nx * 0.3, nz * 0.3, 8, 0.55);
                cavernHeightRadius = Math.Pow(cavernHeightRadius, 2.5) * 220 / 2;
                double cavernCenterY = 20 + JsRandom.Fbm(cavernYNoise, nx * 0.2, nz * 0.2, 3, 0.5) * 60;
                double groundHeight = 10 + JsRandom.Fbm(noise, nx * 0.1, nz * 0.1, 6, 0.5) * 20;
                double height = Math.Max(mountainHeight, groundHeight);
                bool isVolcano = mountainHeight > 100 && JsRandom.Fbm(noise, nx * 0.8, nz * 0.8, 4, 0.6) > 0.6;

                if (isVolcano)
                {
                    double peak = mountainHeight;
                    double craterRadius = 20 + JsRandom.Fbm(noise, nx, nz, 2, 0.5) * 15;
                    double craterDepth = 15 + JsRandom.Fbm(noise, nz, nx, 2, 0.5) * 10;
                    double distFromPeakCenter = JsRandom.Hypot(wx - (cx * ChunkSize + 8), wz - (cz * ChunkSize + 8));
                    if (distFromPeakCenter < craterRadius)
                    {
                        double t = distFromPeakCenter / craterRadius;
                        double craterFloor = peak - craterDepth * (1 - t * t * t);
                        height = Math.Min(height, craterFloor);
                        double lavaLevel = peak - craterDepth + 5;
                        if (height < lavaLevel)
                        {
                            for (long y = (long)Math.Floor(height) + 1; y <= Math.Floor(lavaLevel); y++)
                                if (y >= 0 && y < MaxHeight) d[Index(lx, (int)y, lz)] = BlockCatalog.Lava;
                        }
                    }
                }

                int h = (int)Math.Max(1, Math.Min(MaxHeight - 1, Math.Floor(height)));
                bool prevWasCavern = false;
                for (int y = 0; y <= h; y++)
                {
                    int id;
                    bool isCavern = false;
                    if (y < h - 10)
                    {
                        id = BlockCatalog.Obsidian;
                        if (y > cavernCenterY - cavernHeightRadius && y < cavernCenterY + cavernHeightRadius)
                        {
                            isCavern = true;
                            id = y < 15 ? BlockCatalog.Water : BlockCatalog.Air;
                        }
                        else if (resourceNoise.Sample(nx * 5, y * 0.2) > 0.85)
                        {
                            id = 125;
                        }
                    }
                    else
                    {
                        id = 4;
                    }
                    if (y == 0) id = 1;
                    d[Index(lx, y, lz)] = (byte)id;

                    if (!isCavern && prevWasCavern && id == BlockCatalog.Obsidian && y > 15)
                    {
                        if (serverRandom.Next() < 0.025)
                        {
                            int length = 1 + (int)Math.Floor(serverRandom.Next() * 11);
                            for (int cl = 1; cl <= length; cl++)
                                if (y - cl > 0 && d[Index(lx, y - cl, lz)] == BlockCatalog.Air) d[Index(lx, y - cl, lz)] = 134;
                        }
                    }
                    prevWasCavern = isCavern;

                    if (id == 4)
                    {
                        double r = resourceNoise.Sample(nx * 2, y * 0.1);
                        if (r > 0.95) d[Index(lx, y, lz)] = 124;
                        else if (r > 0.92) d[Index(lx, y, lz)] = 11;
                    }
                }

                if (h < vulcanSeaLevel + 4)
                {
                    for (int y = h; y > h - 4 && y > 0; y--) d[Index(lx, y, lz)] = 5;
                    if (h < vulcanSeaLevel)
                        for (int y = h + 1; y <= vulcanSeaLevel; y++) d[Index(lx, y, lz)] = BlockCatalog.Water;
                }
            }
        }

        AddSeaweedPatches(d, seed, baseX, baseZ, vulcanSeaLevel, 16);

        int lavaCount = 0;
        double totalX = 0, totalY = 0, totalZ = 0;
        for (int y = 60; y < MaxHeight; y++)
        {
            for (int lz = 0; lz < ChunkSize; lz++)
            {
                for (int lx = 0; lx < ChunkSize; lx++)
                {
                    if (d[Index(lx, y, lz)] != BlockCatalog.Lava) continue;
                    lavaCount++;
                    totalX += baseX + lx;
                    totalY += y;
                    totalZ += baseZ + lz;
                }
            }
        }
        return lavaCount > 50 ? new VolcanoInfo(totalX / lavaCount, totalY / lavaCount, totalZ / lavaCount, lavaCount, chunkKey) : null;
    }
}
