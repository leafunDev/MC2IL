using System;
using System.Collections.Generic;
using System.Linq;

namespace MC2IL.Core.Api;

public static class ProcessingLayerRegistry
{
    private static readonly Dictionary<string, MC2ILProcessingLayer> _processingLayersById = new();

    public static IReadOnlyList<MC2ILProcessingLayer> AllProcessingLayers => _processingLayersById.Values.ToList();

    public static void Register<T>() where T : MC2ILProcessingLayer, new()
    {
        var layer = new T();
        _processingLayersById.Add(layer.Id, layer);
    }

    public static MC2ILProcessingLayer GetById(string id)
        => _processingLayersById.TryGetValue(id, out var ret) ? ret : throw new ArgumentException($"No processing layer with id {id} registered");
}
