using System;

namespace Kylin.DI
{
    [AttributeUsage(AttributeTargets.Class)]
    public class ViewModelAttribute : Attribute
    {
        public bool IsGlobal { get; }
        public string[] SceneNames { get; }

        public ViewModelAttribute(bool isGlobal = false, params string[] sceneNames)
        {
            IsGlobal = isGlobal;
            SceneNames = sceneNames ?? Array.Empty<string>();
        }
    }
}