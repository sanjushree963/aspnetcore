// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
    private IReadOnlyDictionary<string, object?>? _latestParameters;

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

    [Inject] public IServiceProvider Services { get; set; } = default!;

    public void Attach(RenderHandle renderHandle)
    {
        _renderHandle = renderHandle;
    }

    public Task SetParametersAsync(ParameterView parameters)
    {
        // We have to snapshot the parameters because ParameterView is like a ref struct - it can't escape the
        // call stack because the underlying buffer may get reused. This is enforced through a runtime check.
        _latestParameters = parameters.ToDictionary();

        if (_prerender)
        {
            _renderHandle.Render(Prerender);
        }

        return Task.CompletedTask;
    }

    private void Prerender(RenderTreeBuilder builder)
    {
        builder.OpenComponent(0, _componentType);

        foreach (var (name, value) in _latestParameters!)
        {
            builder.AddComponentParameter(1, name, value);
        }

        // Avoid infinite recursion by explicitly disabling any component-level render mode
        // on the prerendered child
        builder.AddComponentRenderMode(2, BypassRenderMode.Instance);

        builder.CloseComponent();
    }

    public (ServerComponentMarker?, WebAssemblyComponentMarker?) ToMarkers(HttpContext httpContext)
    {
        var parameters = _latestParameters is null
            ? ParameterView.Empty
            : ParameterView.FromDictionary((IDictionary<string, object?>)_latestParameters);

        ServerComponentMarker? serverMarker = null;
        if (_renderMode is ServerRenderMode || _renderMode is AutoRenderMode)
        {
            // Lazy because we don't actually want to require a whole chain of services including Data Protection
            // to be required unless you actually use Server render mode.
            var serverComponentSerializer = Services.GetRequiredService<ServerComponentSerializer>();
            var invocationId = EndpointHtmlRenderer.GetOrCreateInvocationId(httpContext);
            serverMarker = serverComponentSerializer.SerializeInvocation(invocationId, _componentType, parameters, _prerender);
        }

        WebAssemblyComponentMarker? webAssemblyMarker = null;
        if (_renderMode is WebAssemblyRenderMode ||  _renderMode is AutoRenderMode)
        {
            webAssemblyMarker = WebAssemblyComponentSerializer.SerializeInvocation(_componentType, parameters, _prerender);
        }

        return (serverMarker, webAssemblyMarker);
    }

    // This is only used internally to Endpoints when prerendering
    public class BypassRenderMode : IComponentRenderMode
    {
        public static BypassRenderMode Instance { get; } = new();
    }
}
