# Author gamescan/forge-contract.json — the WeaponForge members this mod reaches into.
#
# WHY THIS IS HAND-WRITTEN, unlike gamescan/contract.json which is extracted from the mod's IL.
# The extractor resolves AccessTools.Method(AccessTools.TypeByName("T"), "M") because both halves
# are literals in one expression. This mod does not call it that way: it caches the Type in a
# static field first and then passes the FIELD to AccessTools.Method, which is a field load in IL
# with no string to recover. Extraction would silently report zero WeaponForge dependencies, and a
# contract that silently finds nothing is worse than no contract at all.
#
# So the list is declared here and VALIDATED against a real manifest at authoring time. Getting a
# key wrong is a build error rather than a dependency that quietly stops being checked.
#
# Run:  python tools/forge-contract.py <manifest.json>   (tools/gamescan.ps1 -Forge does this)
import json, sys, datetime

# member key -> (how we reach it, which of our types depends on it, source file)
USES = {
    # ---- ForgeBridge: detection, loot suppression, and "which modules are theirs" -------------
    "WeaponForge.ForgeRegistry": ("accesstools-typebyname", "ForgeBridge.Probe"),
    "WeaponForge.ForgeRegistry::System.Collections.Generic.IEnumerable`1<WeaponForge.ForgeEntry> Entries":
        ("accesstools-property", "ForgeBridge.CollectForgeIds"),
    "WeaponForge.ForgeEntry::ModuleData module":
        ("accesstools-field", "ForgeBridge.CollectForgeIds"),
    "WeaponForge.ForgeLootPatch": ("accesstools-typebyname", "ForgeBridge.Probe"),
    "WeaponForge.ForgeLootPatch::System.Void Prefix(DropTable)":
        ("harmony-patch", "ForgeBridge.ApplySuppressionPatch"),
    "WeaponForge.ForgeLootPatch::System.Collections.Generic.HashSet`1<DropTableWeightedGroup> _done":
        ("accesstools-field", "ForgeBridge.ClearLootCache"),

    # ---- ForgeContentSwap: the three roots we redirect ----------------------------------------
    # These three are the whole reason the swap works without an upstream change. If any one of
    # them moves, host content silently stops being applied.
    "WeaponForge.ForgeRegistry::System.String WeaponsFolder()":
        ("harmony-postfix", "ForgeContentSwap.ApplyRootPatches"),
    "WeaponForge.ForgeSpriteLibrary::System.String SpritesFolder()":
        ("harmony-postfix", "ForgeContentSwap.ApplyRootPatches"),
    "WeaponForge.ForgeSoundLibrary::System.String SoundsFolder()":
        ("harmony-postfix", "ForgeContentSwap.ApplyRootPatches"),

    # ---- ForgeContentSwap: rebuilding after a swap --------------------------------------------
    "WeaponForge.ForgeRegistry::System.Void BuildAll()":
        ("accesstools-method", "ForgeContentSwap.Reload"),
    "WeaponForge.ForgeRegistry::System.Void RegisterInto(ModuleRegistry)":
        ("accesstools-method", "ForgeContentSwap.Reload"),

    # ---- ForgeContentSwap: private state we clear ---------------------------------------------
    # The riskiest entries in this file. Nothing obliges another mod to keep its private fields,
    # and losing one does not throw — the swap just quietly loads new weapons against old sprites.
    # That is precisely the failure this contract exists to catch before a player does.
    "WeaponForge.ForgeRegistry::System.Collections.Generic.List`1<WeaponForge.ForgeEntry> _entries":
        ("accesstools-field", "ForgeContentSwap.Reload"),
    "WeaponForge.ForgeRegistry::System.Collections.Generic.HashSet`1<System.String> _builtNames":
        ("accesstools-field", "ForgeContentSwap.Reload"),
    "WeaponForge.ForgeSpriteLibrary::System.Boolean _loaded":
        ("accesstools-field", "ForgeContentSwap.ResetLoader"),
    "WeaponForge.ForgeSpriteLibrary::System.Collections.Generic.Dictionary`2<System.String,UnityEngine.Sprite> _sprites":
        ("accesstools-field", "ForgeContentSwap.ResetLoader"),
    "WeaponForge.ForgeSpriteLibrary::System.Collections.Generic.Dictionary`2<System.String,WeaponForge.ForgeSpriteLibrary/SpriteAnim> _anims":
        ("accesstools-field", "ForgeContentSwap.ResetLoader"),
    "WeaponForge.ForgeSpriteLibrary::System.Collections.Generic.Dictionary`2<System.String,UnityEngine.Texture2D> _sheets":
        ("accesstools-field", "ForgeContentSwap.ResetLoader"),
    "WeaponForge.ForgeSoundLibrary::System.Boolean _loaded":
        ("accesstools-field", "ForgeContentSwap.ResetLoader"),
    "WeaponForge.ForgeSoundLibrary::System.Collections.Generic.Dictionary`2<System.String,WeaponForge.ForgeSoundLibrary/Entry> _entries":
        ("accesstools-field", "ForgeContentSwap.ResetLoader"),
}

SOURCE = {
    "ForgeBridge": "src/Content/ForgeBridge.cs",
    "ForgeContentSwap": "src/Content/ForgeContentSwap.cs",
}


def main():
    if len(sys.argv) < 3:
        print("usage: forge-contract.py <manifest.json> <out.json>")
        return 1
    manifest = json.load(open(sys.argv[1], encoding="utf-8"))
    types = manifest["Types"]

    missing = []
    for key in USES:
        if "::" in key:
            t, member = key.split("::", 1)
            if t not in types or member not in types[t]["Members"]:
                missing.append(key)
        elif key not in types:
            missing.append(key)

    if missing:
        print(f"forge-contract: {len(missing)} declared member(s) are NOT in this WeaponForge build:")
        for m in missing:
            print(f"  {m}")
        print("forge-contract: either WeaponForge changed shape (fix the mod, then this list),")
        print("                or a key here is a typo. Not writing a contract that is already wrong.")
        return 4

    uses = {}
    for key, (via, frm) in USES.items():
        owner = frm.split(".", 1)[0]
        uses[key] = [{
            "Via": via,
            "FromMember": f"PunkMultiverse.Content.{frm}",
            "SourceFile": SOURCE.get(owner, ""),
            "SourceLine": 0,
        }]

    contract = {
        "FormatVersion": 1,
        "ModAssembly": "PunkMultiverse",
        "ModVersion": "hand-authored",
        "CapturedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "Uses": uses,
    }
    with open(sys.argv[2], "w", encoding="utf-8") as f:
        json.dump(contract, f, indent=2)
    print(f"forge-contract: {len(USES)} member(s) verified against this build, wrote {sys.argv[2]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
