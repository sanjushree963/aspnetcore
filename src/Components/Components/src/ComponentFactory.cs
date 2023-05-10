// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Components.Reflection;
using Microsoft.AspNetCore.Components.RenderTree;
using static Microsoft.AspNetCore.Internal.LinkerFlags;

namespace Microsoft.AspNetCore.Components;

internal sealed class ComponentFactory
{
    private const BindingFlags _injectablePropertyBindingFlags
        = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly ConcurrentDictionary<Type, ComponentTypeInfoCacheEntry> _cachedComponentTypeInfo = new();

    private readonly IComponentActivator _componentActivator;
    private readonly Renderer _renderer;

    public ComponentFactory(IComponentActivator componentActivator, Renderer renderer)
    {
        _componentActivator = componentActivator ?? throw new ArgumentNullException(nameof(componentActivator));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public static void ClearCache() => _cachedComponentTypeInfo.Clear();

    private static ComponentTypeInfoCacheEntry GetComponentTypeInfo(Type componentType) =>
        _cachedComponentTypeInfo.GetOrAdd(componentType, static ([DynamicallyAccessedMembers(Component)] componentType) =>
            new ComponentTypeInfoCacheEntry(GetRenderModeFromComponentType(componentType), CreatePropertyInjector(componentType)));

    public IComponent InstantiateComponent(IServiceProvider serviceProvider, [DynamicallyAccessedMembers(Component)] Type componentType, IComponentRenderMode? callerSpecifiedRenderMode)
    {
        var componentTypeInfo = GetComponentTypeInfo(componentType);
        var component = callerSpecifiedRenderMode is null && componentTypeInfo.RenderMode is null
            ? _componentActivator.CreateInstance(componentType)
            : _renderer.InstantiateComponentForRenderMode(componentType, componentTypeInfo.RenderMode, callerSpecifiedRenderMode, _componentActivator);

        if (component is null)
        {
            // The default activator will never do this, but an externally-supplied one might
            throw new InvalidOperationException($"The component activator returned a null value for a component of type {componentType.FullName}.");
        }

        componentTypeInfo.PerformPropertyInjection(serviceProvider, component);
        return component;
    }

    private static IComponentRenderMode? GetRenderModeFromComponentType(Type componentType)
        => componentType.GetCustomAttribute<RenderModeAttribute>()?.Mode;

    private static Action<IServiceProvider, IComponent> CreatePropertyInjector([DynamicallyAccessedMembers(Component)] Type type)
    {
        // Do all the reflection up front
        List<(string name, Type propertyType, PropertySetter setter)>? injectables = null;
        foreach (var property in MemberAssignment.GetPropertiesIncludingInherited(type, _injectablePropertyBindingFlags))
        {
            if (!property.IsDefined(typeof(InjectAttribute)))
            {
                continue;
            }

            injectables ??= new();
            injectables.Add((property.Name, property.PropertyType, new PropertySetter(type, property)));
        }

        if (injectables is null)
        {
            return static (_, _) => { };
        }

        return Initialize;

        // Return an action whose closure can write all the injected properties
        // without any further reflection calls (just typecasts)
        void Initialize(IServiceProvider serviceProvider, IComponent component)
        {
            foreach (var (propertyName, propertyType, setter) in injectables)
            {
                var serviceInstance = serviceProvider.GetService(propertyType);
                if (serviceInstance == null)
                {
                    throw new InvalidOperationException($"Cannot provide a value for property " +
                        $"'{propertyName}' on type '{type.FullName}'. There is no " +
                        $"registered service of type '{propertyType}'.");
                }

                setter.SetValue(component, serviceInstance);
            }
        }
    }

    // Tracks information about a specific component type that ComponentFactory uses
    private record class ComponentTypeInfoCacheEntry(
        IComponentRenderMode? RenderMode,
        Action<IServiceProvider, IComponent> PerformPropertyInjection);
}
