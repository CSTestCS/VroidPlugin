# VRoid Plus

An unofficial plugin for the **Steam version of VRoid Studio** (1.26.1 or newer) that adds:

| Feature | Where |
|---|---|
| **Custom face sets**: save every face selection and slider under a name and load it onto any model, like the built-in face sets | *Face Sets* tab |
| **Hair merge**: add the hair groups of any hairstyle (built-in or custom; Front, Back, Overall Hair, Extensions, Side, Ahoge, Extra) to the hairstyle you're editing, keeping its own groups | *Hair Merge* tab |
| **Hair anchor bones**: make a hair group follow any bone on the model instead of the head | *Hair Anchors* tab |
| **Extra body sliders**: Bicep, Forearm, Thigh and Calf size, plus 19 more under *More sliders* (head, neck, torso, arms, hands, fingers, legs, feet, overall). Saved per `.vroid` file | *Body* tab |
| **Accessory anchor bones**: same as hair anchors, for accessories | *Accessories* tab |
| **Expanded pose editor**: *Show Controller* starts off, and you can load **.fbx** poses and animations (play/pause, like built-in animations) | *Pose* tab |

> VRoid Plus is a fan-made mod. It isn't made or supported by pixiv/VRoid. Back up your `.vroid` files before using it.

---

## Install (one double-click)

1. Download this project: green **Code** button → **Download ZIP**. Extract the ZIP anywhere (for example your Desktop).
2. Make sure VRoid Studio is **closed**.
3. Double-click **`Install-VRoidPlus.bat`**.

The installer does everything else:

- Finds VRoid Studio in your Steam library. If it can't, it asks you for the folder.
- Installs [BepInEx 6](https://github.com/BepInEx/BepInEx), the mod loader.
- Starts VRoid Studio **once** so BepInEx can prepare itself. This takes 1–5 minutes, and VRoid closes on its own.
- Downloads a private copy of the .NET build tools (about 200 MB, only the first time; no admin rights needed).
- Builds VRoid Plus for *your* exact VRoid version and puts it in place.

- Starts VRoid Studio and checks that VRoid Plus loaded, then tells you whether it worked.

After that, start VRoid Studio from Steam as usual. To open VRoid Plus, click **VRoid+** in the bottom-left corner or press **Ctrl+Shift+V**. You can change the shortcut in the *Setup* tab.

**After a VRoid Studio update**, if VRoid Plus stops working, run `Install-VRoidPlus.bat` again.

**If something goes wrong**, the installer says so and keeps its window open. Send `%LOCALAPPDATA%\VRoidPlus\install-log.txt` and `%LOCALAPPDATA%\VRoidPlus\BepInEx-LogOutput.log`; you can paste these paths into the File Explorer address bar.

**To uninstall**, run `Uninstall-VRoidPlus.bat`. It removes the plugin, and optionally BepInEx too. Your saved face sets and hair library are backed up to `Documents\VRoidPlus-backup`.

> Windows SmartScreen may warn about the `.bat` file because it was downloaded. Click **More info → Run anyway**.
> Some antivirus tools flag BepInEx's `winhttp.dll`. This is a known false positive for mod loaders.

---

## Using the features

### Face Sets
Type a name and press **Save current face** while editing a model. Press **Load** next to a saved set to apply it to the current model. Files are stored in `VRoid Studio\BepInEx\config\VRoidPlus\FaceSets\`, so you can share them.

### Hair Merge
1. In VRoid's hairstyle editor, open the hairstyle you want to take hair groups **from**. This can be any built-in or custom hairstyle in any slot. In VRoid Plus, give it a name and press **Copy to library**.
2. Open the hairstyle you want to add them **to** and press **Add to this hairstyle** on the library entry.

The edited hairstyle keeps all its own groups and gains the imported ones. You can repeat this as often as you like, with as many hairstyles as you like.

### Hair Anchors / Accessories
Each hair group (as VRoid's hair editor lists them, with or without physics) or accessory is listed with the bone it currently follows. Press **Choose bone…** and pick any bone. The list shows the main skeleton by default; untick *Main skeleton bones only* to see every bone. **Reset** puts it back on its original bone. VRoid rebuilds parts whenever you edit them, and VRoid Plus re-applies your choice automatically.

If a part is missing from the list, tick **Show every mesh**.

### Body
Bicep, Forearm, Thigh and Calf size are always shown. Press **More sliders...** for 19 more, grouped as:

- **Head & neck:** Head Size, Neck Thickness
- **Torso:** Shoulder, Upper Chest, Chest, Waist, Hip, Butt, Whole Torso Thickness
- **Arms & hands:** Whole Arm Thickness, Hand Size, Hand Thickness, Finger Thickness, Thumb Thickness
- **Legs & feet:** Whole Leg Thickness, Foot Size, Foot Width, Toe Size
- **Overall:** Overall Thickness

Every slider goes from 50% to 200%, and sliders acting on the same body part combine. Thickness sliders reshape the mesh around each bone; Size sliders scale the whole part evenly (fingers and toes included). Clothes follow, and the changes show in photo mode and in exports.

Slider values and anchor choices belong to the open `.vroid` file. They're kept while you edit and stored only when you save the file in VRoid (Save or Save As). Opening another file loads its own settings; a new model starts with none. They're stored in `BepInEx\config\VRoidPlus\models\`, not inside the `.vroid` itself, so they stay on this PC.

### Pose
- **Show Controller** is switched off each time the pose editor opens. You can turn it on again as usual. You can disable this behaviour in the Pose tab.
- **Load .fbx file…** loads a pose or animation from a binary FBX (Mixamo, Blender, Maya, etc.). Animations play like VRoid's built-in ones: you get Play/Pause (and a choice of animation if the file has several), and the pose isn't editable while it's loaded. Press **Unload** to go back to normal posing.
  Bones are matched by name: Mixamo, VRoid/VRM, Unity-style, 3ds Max Biped and most common rigs work. Text (ASCII) FBX files aren't supported; re-export them as binary.

---

## Mapping (Face Sets and Hair Merge)

Body sliders, anchors and FBX poses only use Unity's standard objects, so they work on any VRoid version.

**Face Sets** and **Hair Merge** have to read and write VRoid's own internal data (the face settings object and the list of hair groups). VRoid's internal names aren't public and can change between versions. The plugin therefore reads their location from `BepInEx\config\VRoidPlus\mapping.json`. Until that's filled in, these two tabs say *"isn't connected for this VRoid version yet"*.

The **Setup** tab has the tools to fill it in:

1. **Search singletons** for a keyword (`Face`, `Hair`, `Avatar`, `Editor`, …) and press **Explore** on a result.
2. Click **›** to walk into objects until you reach the face settings object, or the list of hair groups of the open hairstyle.
3. Press **Use as Face data** or **Use as Hair group list**.
4. Optionally, set a *refresh* method (a path ending in `()`) that VRoid Plus calls after applying changes, so VRoid redraws the model.

Once a working `mapping.json` exists for a VRoid version, it can be shared, and it's meant to ship with the plugin. **Write type dump** saves a list of VRoid's internal types to `type-dump.txt`, which helps whoever creates the mapping.

Path syntax: `Namespace.Type::StaticMember.Member.List[0].Method()`, or `$find:Namespace.ComponentType::Member` to start from a live component in the scene.

---

## Known limitations

- **Exporting (VRM) and saving (.vroid)**: anchor bones, body sliders and FBX poses act on the model in VRoid's 3D view. Whether VRoid's exporter keeps them depends on how it rebuilds the model at export time, and that hasn't been verified yet. Check your exported model. Face sets and imported hair groups are written into VRoid's own data, so they save and export normally.
- Body slider values and anchor choices are stored in VRoid Plus's settings (`BepInEx\config\VRoidPlus\`), not inside the `.vroid` file. They apply to whichever model is open.
- Hair vertices that VRoid skins directly to the head bone (not to the hair's own bones) stay on the head.
- The window is drawn with basic Unity drawing calls only, because VRoid's Unity build lacks most of the usual UI toolkit (GUILayout). If a control isn't available it switches to a simpler version and notes it in `BepInEx\LogOutput.log`.

---

## For developers

```
src/VRoidPlus/
  Plugin.cs                 BepInEx entry point + the injected MonoBehaviour
  Core/Compat.cs            everything IL2CPP-specific lives here
  Core/Avatar.cs            finds the edited avatar and its bones
  Core/ReflectionPath.cs    resolves mapping.json paths
  Core/ObjectSnapshot.cs    generic object <-> JSON (face sets, hair groups)
  Core/Explorer.cs          type dump + object browser for the Setup tab
  Features/                 one class per feature
  Fbx/                      binary FBX reader, scene evaluation, humanoid retargeting
  UI/                       IMGUI window, Windows file dialog
installer/                  PowerShell installer / uninstaller
```

- **Real build** (what the installer runs): `dotnet build src/VRoidPlus/VRoidPlus.csproj -c Release -p:GameDir="C:\...\VRoid Studio"`. This compiles against the interop assemblies BepInEx generated in `BepInEx\interop`, and defines `IL2CPP`.
- **Check build** (no game needed): `dotnet build src/VRoidPlus/VRoidPlus.csproj`. This compiles against public Unity 2022.3 reference assemblies to catch errors. The output isn't a working plugin.
- If a `prebuilt/VRoidPlus.dll` exists, the installer uses it when building fails.
