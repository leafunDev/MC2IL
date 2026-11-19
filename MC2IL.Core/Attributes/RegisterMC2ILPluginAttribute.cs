using System;
using System.Diagnostics.CodeAnalysis;
using MC2IL.Core.Api;

namespace MC2IL.Core.Attributes;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public class RegisterMC2ILPluginAttribute : Attribute
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type PluginType { get; }

    public RegisterMC2ILPluginAttribute([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pluginType)
    {
        if (!typeof(MC2ILPlugin).IsAssignableFrom(pluginType))
            throw new ArgumentException("Plugin type to register must extend MC2ILPlugin", nameof(pluginType));

        if (pluginType.GetConstructor(Type.EmptyTypes) is null)
            throw new ArgumentException("Plugin type to register must have a public, zero-argument constructor.", nameof(pluginType));

        PluginType = pluginType;
    }
}
