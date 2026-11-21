using System;
using System.Linq;
using MC2IL.Core.Api;
using MC2IL.Core.Attributes;
using MC2IL.Core.Logging;
using MC2IL.Core.Model.Contexts;

[assembly: RegisterMC2ILPlugin(typeof(MC2IL.MirrorSerializerDiscovery.MirrorSerializerDiscoveryPlugin))]

namespace MC2IL.MirrorSerializerDiscovery;

public sealed class MirrorSerializerDiscoveryPlugin : MC2ILPlugin
{
    public override string Name => "Mirror serializer discovery";

    public override string Description => "Finds Mirror-related serialization methods for plugin development.";

    public override void OnLoad() => RegisterProcessingLayer<MirrorSerializerDiscoveryLayer>();
}

/// <summary>
/// A safe starting point for game-specific plugins: locate candidate methods before writing
/// a recovery or removal pass. This example only reports matches and does not change output.
/// </summary>
public sealed class MirrorSerializerDiscoveryLayer : MC2ILProcessingLayer
{
    public override string Name => "Mirror serializer discovery";

    public override string Id => "mirror-serializer-discovery";

    public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
    {
        var matches = appContext.Assemblies
            .SelectMany(assembly => assembly.Types)
            .SelectMany(type => type.Methods.Select(method => (Type: type, Method: method)))
            .Where(candidate => Contains(candidate.Type.FullName, "Mirror")
                || Contains(candidate.Method.Name, "Serialize")
                || Contains(candidate.Method.Name, "Deserialize"))
            .ToList();

        foreach (var candidate in matches)
            Logger.InfoNewline($"Candidate: {candidate.Method.FullNameWithSignature}", Id);

        Logger.InfoNewline($"Found {matches.Count} candidate methods. This discovery layer does not modify them.", Id);
        progressCallback?.Invoke(1, 1);
    }

    private static bool Contains(string value, string fragment) =>
        value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
}
