#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using FloppyDogTools.Tools.PhysBoneToMagica2;

namespace FloppyDogTools.Tools.PhysBoneToMagica2
{
    public class PhysBoneToMagica2Window : EditorWindow
    {
        private GameObject targetRoot;
        private bool includeInactive = true;
        private bool deletePhysBonesAfter = false;

        [MenuItem("Tools/PhysBone → MagicaCloth2")]
        private static void Open()
        {
            GetWindow<PhysBoneToMagica2Window>("PhysBone → Magica2");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("PhysBone → MagicaCloth2 (Bone Cloth)", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            targetRoot = (GameObject)EditorGUILayout.ObjectField(
                "Avatar Root",
                targetRoot,
                typeof(GameObject),
                true
            );

            includeInactive = EditorGUILayout.Toggle(
                "Include Inactive",
                includeInactive
            );

            deletePhysBonesAfter = EditorGUILayout.Toggle(
                "Delete PhysBones After",
                deletePhysBonesAfter
            );

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(targetRoot == null))
            {
                if (GUILayout.Button("Convert", GUILayout.Height(30)))
                {
                    var result = PhysBoneToMagica2Utility.Convert(
                        targetRoot,
                        includeInactive,
                        deletePhysBonesAfter
                    );

                    EditorUtility.DisplayDialog(
                        "PhysBone → MagicaCloth2",
                        $"PhysBones Found: {result.physBonesFound}\n" +
                        $"MagicaCloth Created: {result.magicaCreated}\n" +
                        $"Phys Colliders Converted: {result.physCollidersConverted}\n" +
                        $"PhysBones Deleted: {result.physBonesDeleted}",
                        "OK"
                    );
                }
            }
        }
    }
}
#endif
