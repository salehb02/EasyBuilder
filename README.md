# **EASY BUILDER**

## **Overview**
EASY BUILDER is a lightweight automation tool that streamlines the Unity build workflow.
Instead of manually changing settings for every build, you set the version, build number and profiles in one window. The tool applies each profile, builds it, and puts your project settings back afterwards.

This removes repetitive manual work, reduces mistakes, and makes managing multiple environments or configurations significantly easier.

Supported targets: **Android** (APK, split APKs, AAB, exported Gradle project) and **iOS** (Xcode project).
Supports Unity 2022.3 and Unity 6.

---

## **Installation**
Install Easy Builder in one of these two ways. Don't use both in the same project.

### **Option 1: `.unitypackage` from Releases**
1. Download the latest `.unitypackage` from the [Releases page](https://github.com/salehb02/EasyBuilder/releases).
2. In Unity, choose **Assets → Import Package → Custom Package…** and select the downloaded file.
3. Keep everything selected and click **Import**.

The files are copied into `Assets/EasyBuilder`, where you can edit them. To update, import a newer release over them.

### **Option 2: Git URL (Package Manager)**
Requires [Git](https://git-scm.com/downloads) installed and on your `PATH`.

1. In Unity, open **Window → Package Manager**.
2. Click **+** → **Add package from git URL…**
3. Paste this URL and click **Add**:
   ```
   https://github.com/salehb02/EasyBuilder.git?path=/Assets/EasyBuilder
   ```

Or add it to `Packages/manifest.json` yourself:
```json
{
  "dependencies": {
    "com.salehb02.easybuilder": "https://github.com/salehb02/EasyBuilder.git?path=/Assets/EasyBuilder"
  }
}
```

To pin a specific version, add a release tag to the end of the URL, for example `…?path=/Assets/EasyBuilder#v2.1`. Without a tag you get the latest commit on the default branch; to update, click **Update** in the Package Manager, or remove and re-add the package.

Files installed through the Package Manager are read-only, including the sample profiles. Create your own profiles with **Create → Easy Builder → New Base Profile** and keystores with **Create → Easy Builder → New Keystore Profile**, or use **New Profile…** and **New Keystore Profile…** in the window.

### **Optional setup**
- **Addressables** and **External Dependency Manager (EDM4U)** are detected automatically; see [Addressables and EDM4U](#addressables-and-edm4u).
- To generate changelogs with AI, install Unity's **AI Assistant** package or the **Claude Code**, **Codex** or **Gemini** command-line tool; see [Auto Generate](#auto-generate).
- If your own scripts use an assembly definition and extend `BaseBuildProfileSO`, add a reference to `EasyBuilder.Editor`.

---

## **How to Open**
**Tools → DevDude → Easy Builder**

---

## **Features**
- One build per profile, in one click
- Settings are checked before anything is built, so a batch doesn't fail halfway through
- **Automatic revert:** package name, product name, icons, AAB/split settings and keystore are restored after every build, including failed and canceled ones
- Set **Version** and **Build Number** from the window
- Per-profile scene list and extra scripting define symbols
- Profile-based keystore selection, with passwords kept out of the asset
- Addressables content build and EDM4U Android dependency resolution, detected automatically
- A summary at the end, with a button to show the builds in their folder, or open the folder automatically
- Changelog grouped by feat / fix / …, with detail levels, generation from git commits with an AI of your choice (Unity AI Assistant, Claude Code, Codex, Gemini), and an optional running `CHANGELOG.md`
- Command-line entry point for CI

---

## **Profiles**
Profiles are `ScriptableObject` assets. Create one with **Create → Easy Builder → New Base Profile**.

### **Base profile settings**
- **Package Name**: application identifier (leave empty to keep the project's)
- **AAB Build** / **Split Architectures** (Android)
- **Override Title**: product name for this profile
- **Override Icon** (Android): adaptive, round and legacy icons. Every density is replaced; a texture left empty keeps the project's icon of that kind
- **Scenes**: scenes for this profile (leave empty to use the scenes enabled in Build Settings)
- **Extra Scripting Defines**: define symbols added for this profile's build only

### **Custom profiles**
Inherit from `BaseBuildProfileSO` (namespace `EasyBuilder`) to add your own settings:

```csharp
using EasyBuilder;
using UnityEditor;
using UnityEngine;

[CreateAssetMenu(menuName = "Easy Builder/My Profile")]
public class MyProfileSO : BaseBuildProfileSO
{
    public override void ApplyProfile()
    {
        base.ApplyProfile(); // call first: it records the settings to restore
        // apply your changes
    }

    public override void RevertProfile()
    {
        // undo your changes
        base.RevertProfile();
    }

    // Optional: change the build options, e.g. options.options |= BuildOptions.CompressWithLz4;
    public override void ConfigureBuildOptions(ref BuildPlayerOptions options) => base.ConfigureBuildOptions(ref options);
}
```

You can also override `Validate(BuildTarget, List<string>)` to report problems before the batch starts.
If your profile scripts live in their own assembly definition, add a reference to `EasyBuilder.Editor`.

---

## **Multi-Profile Build**
The window builds every selected profile in order:
1. Apply the profile
2. Build it
3. Revert the profile's changes

The batch stops at the first failed or canceled build. All builds go into the folder you pick, named
`<package>_v<version>_<build number>_<profile name>` (`.apk` or `.aab` for single-file Android builds; split APKs, exported Gradle projects and Xcode projects are folders).

Turn on **Open Folder When Done** in the Output section to show the build folder as soon as the batch finishes (when at least one build succeeded).

The size in the summary is the size of the APK, AAB or project folder that was written to disk.

The profile selection is saved per user in `UserSettings/EasyBuilderSettings.asset`, which Unity's default `.gitignore` excludes.

---

## **Changelog**
Write what changed in the **Changelog** section of the window, one change per line. Start a line with its type to group it:

```
feat: New level select
fix(ui): Button overlap on small screens
perf: Faster loading
refactor!: New save format
  Old saves are migrated on first launch
Tweaked the tutorial text
```

After the batch, Easy Builder writes only your notes, grouped and sorted, to `Changelog_v<version>_<build number>.md` in the output folder:

| Group | Types | Shown from |
|---|---|---|
| Features | `feat` | Brief |
| Fixes | `fix` | Brief |
| Performance | `perf` | Normal |
| Changes | `refactor`, `change`, `improve`, `update`, `style`, `revert` | Normal |
| Documentation | `docs` | Detailed |
| Maintenance | `chore`, `build`, `ci`, `test`, `deps` | Detailed |
| Other | lines without a type | Brief |

- **Detail Level** picks which groups are written. *Normal* also shows scopes (`ui:`), and *Detailed* also includes the indented detail lines. Breaking changes (`type!:`) are always listed.
- A markdown heading such as `## Fixes` puts the untagged lines under it into that group, so you can paste an existing changelog.
- Leave the notes empty to skip the changelog.
- The notes stay in the window after a build, so update them before building the next version.
- The notes, detail level, history file, AI choice and timeout are saved per user in `UserSettings/EasyBuilderSettings.asset`, like the profile selection.

### **Auto Generate**
Pick an AI in the **AI** list at the top of the Changelog section and click **Generate**. The list always shows every supported AI; one that isn't installed is marked *(not installed)*, with a note on how to install it, and can't be used until it is.

| AI | Installed when |
|---|---|
| Unity AI Assistant | The `com.unity.ai.assistant` package (2.19 or newer) is installed. Needs you to be signed in to Unity. |
| Claude Code | `claude` is installed (on the `PATH`, or in `~/.local/bin`). |
| Codex CLI | `codex` is installed. |
| Gemini CLI | `gemini` is installed. |

Command-line tools use your own login or API key, so sign in to them once in a terminal first. They run in the temp folder with their tools turned off, so they can't read or change your project.

**Generate** reads the git commits in the **Commits** range (`From → To`) and asks the selected AI to write the notes at the selected detail level. You can replace your notes or add the generated ones below them. Review the notes before building.

Both fields take a commit hash, tag or branch, typed in or chosen with **▾** from the recent tags and commits. Both ends are included: From `a1b2c3d` and To `e4f5a6b` reads those two commits and everything between them.

- **From** empty: starts after the newest tag before To, leaving out the tagged commit since it belongs to the previous release (or takes the last 200 commits when there is no tag).
- **To** empty: ends at the current commit (HEAD).

For the notes of one release, leave From empty and set To to its tag (e.g. `v2.1`). Setting From to `v2.0` would also include the commit tagged `v2.0`.

While it runs, the button turns into **Cancel** and counts the seconds. **AI Timeout (s)** under **Options** (120 by default) stops waiting after that many seconds and stops the tool.

Auto Generate is only available in the window. On CI, pass notes with `-ebChangelogFile` instead.

### **History file**
Set **History File** under **Options** (for example `CHANGELOG.md`, relative to the project folder) to also keep a running changelog. When every profile builds, an entry is added at the top:

```markdown
# Changelog

## v1.2.0 (build 42) - 2026-11-24

### Features

- New level select

### Fixes

- **ui:** Button overlap on small screens
```

Rebuilding the same version and build number replaces its entry instead of adding a second one. Nothing is added when a build fails or the notes are empty. On CI, pass the notes with `-ebChangelog` or `-ebChangelogFile` (see below).

---

## **Keystore**
Create a keystore profile with **Create → Easy Builder → New Keystore Profile** and select it in the window.
The asset stores only the keystore path and alias. Passwords are read from, in order:
1. The environment variables named in the profile (`EASYBUILDER_KEYSTORE_PASS` and `EASYBUILDER_KEYALIAS_PASS` by default), for CI
2. The passwords entered in the profile's inspector, which are saved in this machine's EditorPrefs

If no keystore profile is selected, the keystore set in Player Settings is used unchanged.
Profiles from v1.x that still hold passwords in the asset keep working, and the inspector offers to move the passwords out.

---

## **Command Line / CI**
```
Unity -batchmode -quit -projectPath . -buildTarget Android ^
      -executeMethod EasyBuilder.BuildRunner.BuildFromCommandLine ^
      -ebProfiles "Assets/Profiles/Store.asset;Assets/Profiles/Demo.asset" ^
      -ebKeystore Assets/Profiles/Release.asset ^
      -ebOutput Builds
```
| Argument | |
|---|---|
| `-ebOutput <folder>` | Required. Where builds are written. |
| `-ebProfiles "<a>;<b>"` | Profile asset paths or GUIDs. Defaults to the window's selection. |
| `-ebKeystore <path or GUID>` | Keystore profile. Defaults to the window's selection. |
| `-ebVersion <version>` | Sets the version before building. |
| `-ebBuildNumber <number>` | Sets the build number before building. |
| `-ebSkipDependencyResolve` | Skips the EDM4U Android dependency resolve. |
| `-ebChangelog "<text>"` | Changelog notes. Write `\n` for a line break. Defaults to the window's notes. |
| `-ebChangelogFile <path>` | Reads the changelog notes from a file instead (relative to the project folder or absolute), for example one your pipeline generates from `git log`. Takes priority over `-ebChangelog`. |
| `-ebChangelogHistory <path>` | Running changelog to add this version to (relative to the project folder or absolute). Defaults to the window's setting. |
| `-ebChangelogDetail <level>` | `Brief`, `Normal` or `Detailed`. Defaults to the window's setting. |

For example, to use the commit messages since the last tag as the notes (bash):
```bash
git log $(git describe --tags --abbrev=0)..HEAD --no-merges --pretty=format:"%s" > notes.md
Unity ... -executeMethod EasyBuilder.BuildRunner.BuildFromCommandLine -ebOutput Builds -ebChangelogFile notes.md -ebChangelogHistory CHANGELOG.md
```

Unity exits with code 0 when every profile built and 1 otherwise.

---

## **Addressables and EDM4U**
- **Addressables** (1.19.11 or newer) are detected automatically, so no define symbol is needed. Content is built before each profile, and *Build Addressables on Player Build* is switched off during the batch so it isn't built twice.
- **External Dependency Manager (EDM4U)** is detected automatically. Android dependencies are force-resolved before each Android build; turn this off in the window if you don't need it.

The `ADDRESSABLE_ACTIVE` and `GOOGLE_PLAY_ACTIVE` define symbols from v1.x are no longer needed.

---

## **Upgrading from v1.x**
- The classes are now in the `EasyBuilder` namespace; add `using EasyBuilder;` to your custom profiles.
- `RevertProfile` is now virtual. Override it to undo your own changes.
- Your profile selection is imported from `Assets/Plugins/buildProfile.txt` the first time you open the window. You can delete that file afterwards.
- Easy Builder no longer turns off the custom keystore when no keystore profile is selected.

---
