using UnityEditor;
using UnityEngine;

[CreateAssetMenu(menuName = "Easy Builder/New Base Profile")]
public class BaseBuildProfileSO : ScriptableObject, IBuildProfile
{
    [SerializeField] private string packageName;
    [SerializeField] private bool aabBuild;
    [SerializeField] private bool splitArchitectures = true;

    public virtual void ApplyProfile()
    {
        Debug.Log($"Applying {name} as active build profile...");

        if (!string.IsNullOrEmpty(packageName))
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, packageName);

        EditorUserBuildSettings.buildAppBundle = aabBuild;

        PlayerSettings.Android.buildApkPerCpuArchitecture = splitArchitectures && !aabBuild;
    }
}