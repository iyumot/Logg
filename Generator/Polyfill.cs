
 
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }


    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
        {
            FeatureName = featureName;
        }
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }

    internal sealed class SetsRequiredMembersAttribute : Attribute { }

    internal sealed class RequiredMemberAttribute : Attribute { }

    internal static class IsExternalInit { }
} 