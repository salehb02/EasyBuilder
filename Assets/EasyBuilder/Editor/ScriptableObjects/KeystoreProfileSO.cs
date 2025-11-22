using UnityEditor;
using UnityEngine;

[CreateAssetMenu(menuName = "Easy Builder/New Keystore Profile")]
public class KeystoreProfileSO : ScriptableObject
{
    [SerializeField] private string path;
    [SerializeField] private string password;
    [Space(2)]
    [SerializeField] private string aliasName;
    [SerializeField] private string aliasPassword;

    public string Path { get => path; }
    public string Password { get => password; }
    public string AliasName { get => aliasName; }
    public string AliasPassword { get => aliasPassword; }

    public void Apply()
    {
        PlayerSettings.Android.useCustomKeystore = true;
        PlayerSettings.Android.keystoreName = Path;
        PlayerSettings.Android.keystorePass = Password;
        PlayerSettings.Android.keyaliasName = AliasName;
        PlayerSettings.Android.keyaliasPass = AliasPassword;
    }

    public static void ClearKeystore()
    {
        PlayerSettings.Android.useCustomKeystore = false;
    }
}