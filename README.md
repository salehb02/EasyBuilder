# **EASY BUILDER**

## **Overview**
EASY BUILDER is a lightweight automation tool that streamlines the Unity build workflow.  
Instead of manually changing settings for every build, you simply enter the version, build number, and selected profiles into a custom window — the tool then automatically applies all required configurations and generates the final build.

This removes repetitive manual work, reduces mistakes, and makes managing multiple environments or configurations significantly easier.

---

## **How to Open**
You can open the Easy Builder window through the Unity menu:

**Tools → Easy Builder**

---

## **Features**
- Automated build processing  
- Profile-based configuration  
- Multi-profile batch builds  
- Organized output folders  
- Profile-based keystore selection  
- Set **Version Name** and **Build Number** directly from the Easy Builder window  
- **Automatic revert** of applied profile settings after each build  

---

## **Profiles**
EASY BUILDER is built around extendable profile definitions created via `ScriptableObject`.

### **Base Profile Includes:**
- Package Name  
- AAB / APK settings  
- Split Architecture  

### **Additional Profile Capabilities**
Custom profiles can extend the system by inheriting from `BaseProfileSO`.  
You can introduce new behavior by adding extra fields and overriding the `ApplyProfile` & `RevertProfile` method.

Profiles can also override:
- **Product Name**  
- **Application Icon**  

Profiles also support:
- **Automatic revert of changes** after a build completes, ensuring your project returns to its original configuration.

This allows each project to fully customize the build process while maintaining a clean, controlled workflow.

---

## **Multi-Profile Build**
EASY BUILDER supports **multiple profiles in one build operation**.  
The system will:
1. Apply each profile sequentially  
2. Generate a separate build for each profile  
3. Revert profile-applied changes  
4. Organize the outputs into structured folders automatically  

---

## **Important Notes**
- If your project uses **Addressables**, add the following to your Script Define Symbols:  
  `ADDRESSABLE_ACTIVE`
- If your project uses Google’s **External Dependency Manager**, add:  
  `GOOGLE_PLAY_ACTIVE`

---
