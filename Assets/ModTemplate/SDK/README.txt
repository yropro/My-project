================================================================================
  STAR VORTEX MOD SDK
================================================================================

  This package contains build tools, language templates, and documentation
  for creating Star Vortex mods. Use alongside ThunderKit which handles
  importing the game's types into your Unity project.


  REQUIREMENTS
  ------------

  - Unity 2022.3.62f2 (MUST match exactly — other versions break AssetBundles)
    https://unity.com/releases/editor/whats-new/2022.3.62

  - Star Vortex installed (ThunderKit imports from the game installation)


  SETUP
  -----

  1. Create a new Unity project using Unity 2022.3.62f2
     Use the 2D (URP) template.

  2. Install ThunderKit:
       Window > Package Manager > + (top left) > Add package from git URL
       Paste: https://github.com/PassivePicasso/ThunderKit.git
       Click Add and wait for it to install.

  3. A ThunderKit Settings window will appear after install.
     Click Browse and select your Star Vortex.exe.
     Click Import.
     When prompted to disable the Assembly Updater, click Restart.
     When prompted that import is complete, click Restart again.

  4. If you're reading this, you've already imported the .unitypackage.
     You should now have build tools, language templates, and mod.json.

  5. Import base game content:
       Star Vortex Mod > Import Game Content
       Point it at your Star Vortex installation folder.
       This imports all game ScriptableObjects into Assets/GameContent/
       so you can browse and inspect every item, squadron, star, and more.

  You now have access to all Star Vortex types:
    Right-click > Create > Star Vortex > Base > ...


  TRANSLATIONS (lang_*.json)
  --------------------------

  Every piece of player-facing text in a mod needs a language entry.
  The SDK can generate your English language file automatically from
  the name, description, and languageKey fields on your ScriptableObjects.

  1. Set a unique languageKey on each ScriptableObject (e.g., "myMod.bat")
  2. Fill in the name and description fields as normal
  3. Star Vortex Mod > Generate Language File

  This scans all ItemBase, Legendary, TemplateBase, and SquadronBase
  assets in the project and writes Assets/Languages/lang_enUS.json.
  Package Mod copies it into your mod folder automatically.

  To add translations for other languages, create additional files in
  Assets/Languages/ (e.g., lang_de.json, lang_fr.json). Format:

    {
      "myMod.bat.name": "Fledermaus",
      "myMod.bat.desc": "Ein fledermausförmiges Tarnschiff."
    }

  Only lang_enUS.json is required. If a key is missing in the player's
  language, the game falls back to English automatically.


  CREATING NEW CONTENT (Asset Mods)
  ---------------------------------

  Asset mods add new weapons, items, ship templates, and more using
  Unity's AssetBundle system. The game automatically merges mod content
  with the base game.

  You can organise your assets however you like in your Unity project.
  The game finds mod content by type, not by folder path. Just make
  sure the resourcePath field on your ScriptableObject is set correctly
  (e.g., "Base/Items/PrimaryWeapon" for a primary weapon).

  Weapon types you can create (Right-click > Create > Star Vortex > Base > Item):
    - Launcher - Basic               Standard single-shot weapon
    - Launcher - Scatter             Fires spread/burst patterns
    - Launcher - Missile             Homing missiles
    - Launcher - Rebound             Bouncing projectiles
    - Launcher - Mine                Deployable mines
    - Launcher - Orb                 Orb-style projectiles
    - Launcher - Spinal              Fixed forward-facing weapons
    - Launcher - Charging            Charge-up weapons
    - Launcher - Toggle              Continuous fire weapons
    - Launcher - Leech               Health-draining weapons
    - Launcher - Auto Basic          Auto-firing turret weapons
    - Launcher - Auto Missile        Auto-firing missile turrets

  Projectile types (Add Component on a prefab):
    - Projectile                     Basic projectile (collision damage)
    - ExplosiveProjectile            Explodes on impact (area damage)
    - ClusterProjectile              Splits into sub-projectiles
    - SeekingProjectile              Homes toward targets
    - Missile                        Guided missile with turning
    - Mine                           Deployable proximity mine
    - FuzzyProjectile                Fuzzy/area projectile
    - LaserProjectile                Laser beam
    - BlackHoleProjectile            Gravity well
    - LeechProjectile                Health drain projectile


  EXAMPLE — Cluster weapon (like a Banana Bomb):

    1. Create the sub-projectile prefab first:
       - Create a new GameObject, add: SpriteRenderer, Rigidbody2D,
         CircleCollider2D, AutoDestroy, ExplosiveProjectile
       - Set the sprite to your banana art
       - Configure ExplosiveProjectile: set explosiveRadius, etc.
       - Set Rigidbody2D: Gravity Scale = 0, Linear Drag = 0
       - Set the collider as a trigger (Is Trigger = true)
       - Save as a prefab (e.g., "Banana Projectile")

    2. Create the cluster projectile prefab:
       - Create a new GameObject, add: SpriteRenderer, Rigidbody2D,
         CircleCollider2D, AutoDestroy, ClusterProjectile
       - Set the sprite to your bomb art
       - In ClusterProjectile: set projectileCount (e.g., 6),
         drag your banana prefab into the "projectile" field,
         set projectileHealth, projectileVelocity, projectileAutoDestroy
       - Save as a prefab (e.g., "Banana Bomb Projectile")

    3. Create the weapon ScriptableObject:
       - Right-click > Create > Star Vortex > Base > Item > Launcher - Scatter
       - Fill in: resourcePath, languageKey, description
       - Set the projectile reference to your Banana Bomb prefab
       - Configure damage, reload time, velocity, etc.

    4. Star Vortex Mod > Generate Language File
       (Automatically creates lang_enUS.json from your assets)

    5. Set AssetBundle name on ALL assets (both prefabs + the ScriptableObject)

    6. Build and package as described below.


  BROWSING & MODIFYING EXISTING CONTENT
  --------------------------------------

  After setup, run "Star Vortex Mod > Import Game Content" and point it
  at your Star Vortex installation folder. This imports all base game
  ScriptableObjects into Assets/GameContent/ where you can inspect every
  value in the Unity Inspector — squadron formations, boss loadouts,
  star designs, item stats, loot tables, and more.

  The GameContent folder is READ-ONLY reference data. To override
  an existing asset:

    1. Find the asset in Assets/GameContent/ (e.g., a SquadronBase)
    2. Select it and press Ctrl+D to duplicate
    3. Move the copy to your own mod's Assets folder
    4. Edit the copy — change any values you want
    5. KEEP THE SAME NAME as the original (this is how the override works)
    6. DO NOT change the resourcePath field (it identifies which asset to replace)
    7. Assign it to your AssetBundle
    8. Build and package your mod as normal

  At runtime, the game loads your version instead of the original.
  This works for any ScriptableObject type: items, squadrons, stars,
  templates, NPCs, missions, loot tables, and more.

  IMPORTANT: The override is matched by asset NAME + resourcePath.
  If you rename the asset, it becomes new content instead of an
  override. If you change the resourcePath, it may override the
  wrong asset or become new content in the wrong category.

  REFERENCES IN GAMECONTENT
  Non-ScriptableObject references (prefabs, sprites, materials, audio)
  show as "Missing" in GameContent because visual assets are not part
  of the data import. This is normal and does NOT affect your mod:

    - All numeric values, booleans, enums, strings, and SO-to-SO
      references (e.g., which TemplateBase a squadron uses, which
      weapons a template has) are fully intact and clickable.

    - When your mod overrides a base game asset, the game engine
      automatically inherits all missing prefab, sprite, material,
      and audio references from the original. You only need to change
      the values you care about — everything else carries over.

    - If you want to replace a visual asset (e.g., give a weapon a
      new projectile prefab or new icon sprite), create the asset in
      your mod project, assign it to the field, and include it in
      your AssetBundle. Your version takes priority over the original.

    - ScriptableObject references are NOT auto-inherited because you
      can set those yourself via GameContent. If you clear an SO
      reference, it stays cleared at runtime.

  For changes that require altering game logic (not just data), use
  code mods with Harmony patches (see CODE MODS below).


  BUILDING YOUR MOD
  -----------------

  1. Select your assets in the Project window. At the bottom of the
     Inspector, set the AssetBundle name (e.g., "bananabomb").

  2. Star Vortex Mod > Build AssetBundles

  3. Edit Assets/mod.json with your mod details (id, name, author, etc.)

  4. Star Vortex Mod > Package Mod
     This creates a ready-to-install mod folder in "ModPackage/"
     and copies your language files automatically.

  5. Copy the mod folder into:
       StarVortex_Data/Mods/YourModName/

  Your mod folder should contain:
    mod.json              Required
    yourmod.bundle        Asset content
    YourMod.dll           Code mod (optional)
    components.json       Component manifest (auto-generated)
    lang_enUS.json        English translations
    lang_*.json           Other languages (optional)
    icon.png              Mod icon, 256x256 (optional)

  6. Launch Star Vortex — your mod loads automatically!


  CODE MODS (Harmony patches)
  ---------------------------

  Code mods can patch any method in the game using HarmonyX. This allows
  modifying existing content, adding new mechanics, UI changes, and more.

  1. Create an Assembly Definition in your scripts folder:
       Right-click > Create > Assembly Definition
       In its Inspector, tick Override References and add:
         - Assembly-CSharp
         - 0Harmony

  2. Create a C# script implementing IStarVortexMod:

       using HarmonyLib;
       using StarVortex;
       using UnityEngine;

       public class MyMod : IStarVortexMod
       {
           public void Init(ModInfo modInfo, Harmony harmony)
           {
               Debug.Log("[MyMod] Initializing...");
               harmony.PatchAll();
           }

           public void OnGameLoaded()
           {
               // Called after game systems are ready
           }

           public void Shutdown()
           {
               // Called when the game exits
           }
       }

  3. Your compiled DLL is at Library/ScriptAssemblies/YourAssembly.dll
     Copy it into your mod folder alongside mod.json.

  For Harmony documentation: https://harmony.pardeike.net/


  MOD.JSON FIELDS
  ---------------

    id            Unique identifier (reverse domain: com.yourname.modname)
    name          Display name in the mod list
    author        Your name or team
    version       Semantic version (1.0.0)
    description   Short description
    gameVersion   Target game version (warning if mismatched, not blocking)
    dependencies  Array of mod IDs this requires (loaded first)
    loadOrder     Lower = loads earlier (use 0 unless you need ordering)
    tags          Freeform tags for categorization


  SHADERS & MATERIALS
  --------------------

  Star Vortex uses URP (Universal Render Pipeline). Your mod project must
  use URP too, or materials will render as bright pink in the build.
  Do NOT use built-in shaders like Sprites/Default — they are stripped
  from URP builds and will not load from AssetBundles.

  The SDK includes pre-configured materials in the Materials/ folder.
  Use these on your prefabs instead of creating materials from scratch:

    Generic HueShift Material       Sprite with hue shift + lighting
    Structure Material              Sprite with hue shift for structures
    Image HueShift Material         Unlit sprite with hue shift
    Image Structure Material        Unlit sprite hue shift for structures
    Sprites-HueShift-BlendModes     Sprite with hue shift + blend modes
    Sprite-Glow                     Glowing sprite (additive)
    Glow Colors (x1.25)             Particle glow, 1.25x intensity
    Glow Colors (x1.5)              Particle glow, 1.5x intensity
    Glow Colors (x2)                Particle glow, 2x intensity

  If you need a plain material without effects, use these URP shaders:
    Sprites:    Universal Render Pipeline/2D/Sprite-Lit-Default
    Particles:  Universal Render Pipeline/Particles/Unlit


  TIPS
  ----

  - Use a .NET decompiler (dnSpy, ILSpy) on Assembly-CSharp.dll to browse
    all game code. Every type, method, and field is readable.

  - 1 Unity unit = 20 metres in-game. Keep this in mind for weapon ranges.

  - Test your mod by dropping it in StarVortex_Data/Mods/ and launching.
    Check the Player.log for [ModLoader] messages.

  - The in-game Mod Panel shows load status and errors for all mods.


================================================================================
