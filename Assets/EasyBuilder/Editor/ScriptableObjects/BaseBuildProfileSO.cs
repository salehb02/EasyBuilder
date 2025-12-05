using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

[CreateAssetMenu(menuName = "Easy Builder/New Base Profile")]
public class BaseBuildProfileSO : ScriptableObject, IBuildProfile
{
    [SerializeField] private string packageName;
    [SerializeField] private bool aabBuild;
    [SerializeField] private bool splitArchitectures = true;

    [Header("App Title")]
    [SerializeField] private bool overrideTitle;
    [SerializeField] private string appTitle;
    private string _currentAppTitle;

    [Header("Icon")]
    [SerializeField] private bool overrideIcon;
    [SerializeField] private Texture2D adaptiveBackground;
    [SerializeField] private Texture2D adaptiveForeground;
    [SerializeField] private Texture2D roundedIcon;
    [SerializeField] private Texture2D legacyIcon;
    private PlatformIcon[] _currentAdaptiveIcons;
    private PlatformIcon[] _currentRoundedIcons;
    private PlatformIcon[] _currentLegacyIcons;

    public virtual void ApplyProfile()
    {
        Debug.Log($"Applying {name} as active build profile...");

        if (!string.IsNullOrEmpty(packageName))
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, packageName);

        EditorUserBuildSettings.buildAppBundle = aabBuild;

        PlayerSettings.Android.buildApkPerCpuArchitecture = splitArchitectures && !aabBuild;

        ApplyTitle();
        ApplyIcon();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private void ApplyTitle()
    {
        if (!overrideTitle)
            return;

        if (string.IsNullOrEmpty(appTitle))
            return;

        Debug.Log($"Applying {name} title...");

        _currentAppTitle = PlayerSettings.productName;
        PlayerSettings.productName = appTitle;
    }

    private void RevertTitle()
    {
        if (!overrideTitle)
            return;

        PlayerSettings.productName = _currentAppTitle;
    }

    private void ApplyIcon()
    {
        if (!overrideIcon)
            return;

        Debug.Log($"Applying {name} icon...");

        // Adaptive
        _currentAdaptiveIcons = PlayerSettings.GetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Adaptive);
        var newAdaptive = PlayerSettings.GetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Adaptive);

        foreach (var item in newAdaptive)
        {
            if (item.width != 432)
                continue;

            item.SetTexture(adaptiveBackground, 0);
            item.SetTexture(adaptiveForeground, 1);
        }

        PlayerSettings.SetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Adaptive, newAdaptive);

        // Round
        _currentRoundedIcons = PlayerSettings.GetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Round);
        var newRounded = PlayerSettings.GetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Round);

        foreach (var item in newRounded)
        {
            if (item.width != 192)
                continue;

            item.SetTexture(roundedIcon, 0);
        }

        PlayerSettings.SetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Round, newRounded);

        // Legacy
        _currentLegacyIcons = PlayerSettings.GetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Legacy);
        var newLegacy = PlayerSettings.GetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Legacy);

        foreach (var item in newLegacy)
        {
            if (item.width != 192)
                continue;

            item.SetTexture(legacyIcon, 0);
        }

        PlayerSettings.SetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Legacy, newLegacy);
    }

    private void RevertIcon()
    {
        if (!overrideIcon)
            return;

        // Adaptive
        PlayerSettings.SetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Adaptive, _currentAdaptiveIcons);

        // Round
        PlayerSettings.SetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Round, _currentRoundedIcons);

        // Legacy
        PlayerSettings.SetPlatformIcons(BuildTargetGroup.Android, AndroidPlatformIconKind.Legacy, _currentLegacyIcons);
    }

    public void RevertProfile()
    {
        RevertIcon();
        RevertTitle();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"Reverting {name} profile...");
    }
}