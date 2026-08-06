using System;
using System.Collections.Generic;
using System.Linq;

namespace GameScan
{
    /// <summary>
    /// Buckets game types into the same domain areas the curated docs use, so the generated
    /// API index sits alongside the prose instead of being one undifferentiated wall.
    ///
    /// Rules are ordered and first-match-wins, so put the specific ones first. This is
    /// navigation aid, not taxonomy — a type landing in "misc" costs nothing.
    /// </summary>
    public static class Areas
    {
        public sealed record Area(string Slug, string Title, string Doc);

        public static readonly Area Misc = new("misc", "Uncategorised", null);

        static readonly (Area Area, string[] Patterns)[] Rules =
        {
            (new("modules-and-ship-building", "Modules & ship building", "modules-and-ship-building.md"),
                new[]{ "Module", "Cluster", "Loadout", "Vault", "Grid", "Augmentation", "AbilitySlot" }),

            (new("shops-and-economy", "Shops, economy & progression", "shops-and-economy.md"),
                new[]{ "Shop", "Price", "MetaProgress", "Consumable", "StationUpgrade", "Leaderboard", "DailyChallenge", "IHasCost" }),

            (new("players-and-projectiles", "Players, weapons & projectiles", "players-and-projectiles.md"),
                new[]{ "Ship", "Projectile", "Weapon", "Shooter", "Minion", "Hook", "Crosshair", "Barrel", "Aim", "Piercing", "Homing", "Impact", "Inertia", "Parabola", "Dash" }),

            (new("enemies-and-ai", "Enemies & AI", "enemies.md"),
                new[]{ "Enemy", "AIAgent", "Vision", "StateMachine", "Action", "Condition", "Movement", "Navigation", "Boss", "Crawler", "Leg", "Eye", "Faction", "Aggro", "Destination", "Unit" }),

            (new("plants", "Plants", "plants.md"),
                new[]{ "Plant", "Fruit", "Ecosystem" }),

            (new("pickups-and-loot", "Pickups & loot", "pickups-and-loot.md"),
                new[]{ "Pickup", "Loot", "Ingredient", "DropTable", "Droppabble", "Grabbable", "Resource" }),

            (new("terrain-and-cells", "Terrain & cells", "terrain.md"),
                new[]{ "Cell", "Tilemap", "Tile", "Ground", "Merged", "Regrow", "Burn", "Edge", "Outline", "Rasteriz" }),

            (new("level-generation", "Level generation", "level-generation.md"),
                new[]{ "Generator", "GeneratorJob", "Dungeon", "Biom", "Biome", "Noise", "Perlin", "Room", "Graph", "PoI", "Heightmap", "Depth", "Seed", "Level", "Segment", "Border", "Intersection", "Crust", "Spreader" }),

            (new("fog-and-lighting", "Fog & lighting", "fog-and-lighting.md"),
                new[]{ "Fog", "Light", "Lightmap", "Blur", "RenderFog", "Shadow" }),

            (new("save-and-serialization", "Save & serialization", "save-and-serialization.md"),
                new[]{ "Savable", "Memento", "ComponentData", "EntityData", "Snapshot", "SaveLoad", "SaveDestroyed", "CLZF2" }),

            (new("entities-and-world", "Entities, interaction & world systems", "entities-and-world.md"),
                new[]{ "Entity", "Interact", "Hazard", "Explosion", "Electricity", "Conductor", "Station", "FastTravel", "Scanner", "Instrument", "Damag", "Damage", "Health", "Shield", "Status", "Push", "Destroy" }),

            (new("ui-and-screens", "UI, screens & HUD", "ui-and-screens.md"),
                new[]{ "Screen", "Widget", "Hud", "Menu", "Popup", "Button", "Slider", "Bar", "Panel", "Prompt", "Hint", "Toggler", "Tab", "Pager", "Fader", "Ui", "Card", "Row", "Item", "Indicator", "Display", "Text", "Selector", "Dropdown", "Healthbar", "Offscreen" }),

            (new("map-and-minimap", "Map & minimap", "map-and-minimap.md"),
                new[]{ "Map", "Minimap" }),

            (new("audio", "Audio & music", "audio.md"),
                new[]{ "Audio", "Music", "Sfx", "Sound", "Wave" }),

            (new("input", "Input & devices", "input.md"),
                new[]{ "Input", "ActionMap", "Cursor", "Device", "Rumble", "VirtualJoy", "Gamepad" }),

            (new("camera", "Camera", "camera.md"),
                new[]{ "Camera", "Ortho", "Shaker", "Shake" }),

            (new("game-state-flow", "Game state & run flow", "game-state-flow.md"),
                new[]{ "GameController", "GameScene", "GameOver", "GameWon", "RunData", "RunSetup", "RunArguments", "RunEnded", "MainMenu", "Splash", "Loading", "Transition", "TimeManager", "TimeScale", "Tutorial", "Installer", "Registry", "Config", "Settings", "Options", "Steam", "Platform", "Analytics", "Singleton" }),

            (new("effects-and-visuals", "Effects & visuals", "effects-and-visuals.md"),
                new[]{ "Effect", "Particle", "Visual", "Animation", "Animator", "Sprite", "Render", "Beam", "Highlight", "Trail", "Blink", "Rotate", "Theme", "Color" }),
        };

        public static Area Classify(string typeName)
        {
            // Nested types belong with their declaring type.
            var slash = typeName.IndexOf('/');
            if (slash >= 0) typeName = typeName.Substring(0, slash);

            var dot = typeName.LastIndexOf('.');
            var simple = dot >= 0 ? typeName.Substring(dot + 1) : typeName;

            foreach (var (area, patterns) in Rules)
                if (patterns.Any(p => simple.Contains(p, StringComparison.Ordinal)))
                    return area;

            return Misc;
        }

        public static IEnumerable<Area> All => Rules.Select(r => r.Area).Append(Misc);
    }
}
