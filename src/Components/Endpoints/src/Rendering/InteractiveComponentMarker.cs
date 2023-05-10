// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Microsoft.AspNetCore.Components.Endpoints;

/// <summary>
/// A component that describes a location in prerendered output where client-side code
/// should insert an interactive component.
/// </summary>
internal class InteractiveComponentMarker : IComponent
{
    private readonly Type _componentType;
    private readonly IComponentRenderMode _renderMode;
    private readonly bool _prerender;
    private RenderHandle _renderHandle;
    private IReadOnlyList<ParameterValue>? _latestParameters;

    public InteractiveComponentMarker(Type componentType, IComponentRenderMode renderMode)
    {
        _componentType = componentType;
        _renderMode = renderMode;
        _prerender = renderMode switch
        {
            ServerRenderMode mode => mode.Prerender,
            WebAssemblyRenderMode mode => mode.Prerender,
            AutoRenderMode mode => mode.Prerender,
            _ => throw new NotSupportedException($"Server-side rendering does not support the render mode '{renderMode}'.")
        };
    }

    public void Attach(RenderHandle renderHandle)
    {
        _renderHandle = renderHandle;
    }

    public Task SetParametersAsync(ParameterView parameters)
    {
        // We have to snapshot the parameters because ParameterView is like a ref struct - it can't escape the
        // call stack because the underlying buffer may get reused. This is enforced through a runtime check.
        _latestParameters = parameters.ToList();

        _renderHandle.Render(BuildRenderTree);
        return Task.CompletedTask;
    }

    private void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (_prerender)
        {
            builder.OpenComponent(0, _componentType);

            foreach (var entry in _latestParameters!)
            {
                builder.AddComponentParameter(1, entry.Name, entry.Value);
            }

            // Avoid infinite recursion by explicitly disabling any component-level render mode
            // on the prerendered child
            builder.AddComponentRenderMode(2, BypassRenderMode.Instance);

            builder.CloseComponent();
        }
    }

    // This is only used internally to Endpoints when prerendering
    public class BypassRenderMode : IComponentRenderMode
    {
        public static BypassRenderMode Instance { get; } = new();
    }
}
