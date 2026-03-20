using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DapMod.Core;

public partial class MainMod
{
    private static string GetComponentTypeName(Component component)
    {
        return component.GetType().FullName ?? component.GetType().Name;
    }

    private static bool HasReflectionChanges(ReflectionComponentState? state)
    {
        return state != null && (state.PropertyStates.Count > 0 || state.RestoreCalls.Count > 0);
    }

    private static void RestoreBehaviourStates(List<BehaviourState> states)
    {
        foreach (BehaviourState state in states)
        {
            if (state.Behaviour != null)
            {
                state.Behaviour.enabled = state.Enabled;
            }
        }
    }

    private static void RestoreReflectionProperties(List<ReflectionComponentState> states)
    {
        foreach (ReflectionComponentState state in states)
        {
            foreach (ReflectionPropertyState propertyState in state.PropertyStates)
            {
                try
                {
                    MethodInfo? setter = propertyState.Property.GetSetMethod(true);
                    setter?.Invoke(state.Component, new[] { propertyState.Value });
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static void InvokeRestoreMethods(List<ReflectionComponentState> states)
    {
        foreach (ReflectionComponentState state in states)
        {
            foreach (ReflectionMethodCallState restoreCall in state.RestoreCalls)
            {
                try
                {
                    restoreCall.Method.Invoke(state.Component, restoreCall.Arguments);
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static bool TrySnapshotProperty(Component component, ref ReflectionComponentState? state, string propertyName, object? replacementValue)
    {
        PropertyInfo? property = component.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        MethodInfo? getter = property?.GetGetMethod(true);
        MethodInfo? setter = property?.GetSetMethod(true);

        if (property == null || getter == null || setter == null)
        {
            return false;
        }

        try
        {
            object? currentValue = getter.Invoke(component, Array.Empty<object>());

            state ??= new ReflectionComponentState
            {
                Component = component
            };

            state.PropertyStates.Add(new ReflectionPropertyState
            {
                Property = property,
                Value = currentValue
            });

            setter.Invoke(component, new[] { replacementValue });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool QueueRestoreMethod(ref ReflectionComponentState? state, Component component, string methodName, params object?[] args)
    {
        MethodInfo? method = FindCompatibleMethod(component.GetType(), methodName, args);
        if (method == null)
        {
            return false;
        }

        state ??= new ReflectionComponentState
        {
            Component = component
        };

        state.RestoreCalls.Add(new ReflectionMethodCallState
        {
            Method = method,
            Arguments = (object?[])args.Clone()
        });

        return true;
    }

    private static bool TryInvokeMethod(Component component, string methodName, params object?[] args)
    {
        MethodInfo? method = FindCompatibleMethod(component.GetType(), methodName, args);
        if (method == null)
        {
            return false;
        }

        try
        {
            method.Invoke(component, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? FindCompatibleMethod(Type type, string methodName, object?[] args)
    {
        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (MethodInfo method in methods)
        {
            if (!method.Name.Equals(methodName, StringComparison.Ordinal))
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != args.Length)
            {
                continue;
            }

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                object? arg = args[i];
                Type parameterType = parameters[i].ParameterType;

                if (arg == null)
                {
                    if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                    {
                        matches = false;
                        break;
                    }

                    continue;
                }

                Type argType = arg.GetType();
                if (!parameterType.IsAssignableFrom(argType))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return method;
            }
        }

        return null;
    }

    private static Component? FindFirstLoadedComponentByTypeName(string fullTypeName)
    {
        Type? runtimeType = ResolveLoadedType(fullTypeName);
        if (runtimeType != null)
        {
            Component? runtimeMatch = FindFirstLoadedComponentByRuntimeType(runtimeType);
            if (runtimeMatch != null)
            {
                return runtimeMatch;
            }
        }

        try
        {
            Component? behaviourMatch = FindComponentByTypeName(UnityEngine.Object.FindObjectsOfType<Behaviour>(true), fullTypeName);
            if (behaviourMatch != null)
            {
                return behaviourMatch;
            }

            return FindComponentByTypeName(UnityEngine.Object.FindObjectsOfType<Component>(true), fullTypeName);
        }
        catch
        {
            try
            {
                return FindComponentByTypeName(Resources.FindObjectsOfTypeAll<Component>(), fullTypeName);
            }
            catch
            {
                return null;
            }
        }
    }

    private static Component? FindFirstLoadedComponentByRuntimeType(Type runtimeType)
    {
        try
        {
            Component? behaviourMatch = FindComponentByRuntimeType(UnityEngine.Object.FindObjectsOfType<Behaviour>(true), runtimeType);
            if (behaviourMatch != null)
            {
                return behaviourMatch;
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            Component? componentMatch = FindComponentByRuntimeType(UnityEngine.Object.FindObjectsOfType<Component>(true), runtimeType);
            if (componentMatch != null)
            {
                return componentMatch;
            }
        }
        catch
        {
            // ignored
        }

        try
        {
            Component? resourceMatch = FindComponentByRuntimeType(Resources.FindObjectsOfTypeAll<Component>(), runtimeType);
            if (resourceMatch != null)
            {
                return resourceMatch;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static Component? FindComponentByTypeName(Component[] components, string fullTypeName)
    {
        foreach (Component component in components)
        {
            if (component == null)
            {
                continue;
            }

            Type type = component.GetType();
            if (string.Equals(type.FullName, fullTypeName, StringComparison.Ordinal))
            {
                return component;
            }
        }

        return null;
    }

    private static Component? FindComponentByRuntimeType(Component[] components, Type runtimeType)
    {
        foreach (Component component in components)
        {
            if (component == null)
            {
                continue;
            }

            Type componentType = component.GetType();
            if (runtimeType.IsAssignableFrom(componentType))
            {
                return component;
            }
        }

        return null;
    }

    private static object? GetObjectMemberValue(object instance, string memberName)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        for (Type? type = instance.GetType(); type != null; type = type.BaseType)
        {
            PropertyInfo? property = type.GetProperty(memberName, Flags);
            MethodInfo? getter = property?.GetGetMethod(true);
            if (getter != null)
            {
                try
                {
                    return getter.Invoke(instance, Array.Empty<object>());
                }
                catch
                {
                    // ignored
                }
            }

            FieldInfo? field = type.GetField(memberName, Flags);
            if (field != null)
            {
                try
                {
                    return field.GetValue(instance);
                }
                catch
                {
                    // ignored
                }
            }
        }

        return null;
    }

    private static object? GetStaticObjectMemberValue(Type type, string memberName)
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        for (Type? current = type; current != null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(memberName, Flags);
            MethodInfo? getter = property?.GetGetMethod(true);
            if (getter != null)
            {
                try
                {
                    return getter.Invoke(null, Array.Empty<object>());
                }
                catch
                {
                    // ignored
                }
            }

            FieldInfo? field = current.GetField(memberName, Flags);
            if (field != null)
            {
                try
                {
                    return field.GetValue(null);
                }
                catch
                {
                    // ignored
                }
            }
        }

        return null;
    }

    private static bool TryAdjustNumericMember(object instance, float delta, params string[] memberNames)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        Type type = instance.GetType();
        foreach (string memberName in memberNames)
        {
            PropertyInfo? property = type.GetProperty(memberName, Flags);
            MethodInfo? getter = property?.GetGetMethod(true);
            MethodInfo? setter = property?.GetSetMethod(true);
            if (property != null && getter != null && setter != null &&
                TryConvertAdjustedValue(getter.Invoke(instance, Array.Empty<object>()), delta, property.PropertyType, out object? adjustedValue))
            {
                try
                {
                    setter.Invoke(instance, new[] { adjustedValue });
                    return true;
                }
                catch
                {
                    // ignored
                }
            }

            FieldInfo? field = type.GetField(memberName, Flags);
            if (field != null &&
                TryConvertAdjustedValue(field.GetValue(instance), delta, field.FieldType, out adjustedValue))
            {
                try
                {
                    field.SetValue(instance, adjustedValue);
                    return true;
                }
                catch
                {
                    // ignored
                }
            }
        }

        return false;
    }

    private static bool TryConvertAdjustedValue(object? currentValue, float delta, Type targetType, out object? adjustedValue)
    {
        adjustedValue = null;
        if (currentValue == null)
        {
            return false;
        }

        try
        {
            if (targetType == typeof(float))
            {
                adjustedValue = Convert.ToSingle(currentValue) + delta;
                return true;
            }

            if (targetType == typeof(double))
            {
                adjustedValue = Convert.ToDouble(currentValue) + delta;
                return true;
            }

            if (targetType == typeof(int))
            {
                adjustedValue = Mathf.RoundToInt(Convert.ToSingle(currentValue) + delta);
                return true;
            }

            if (targetType == typeof(long))
            {
                adjustedValue = Convert.ToInt64(Mathf.RoundToInt(Convert.ToSingle(currentValue) + delta));
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static bool TryInvokeObjectMethod(object instance, string methodName, params object?[] args)
    {
        MethodInfo? method = FindCompatibleMethod(instance.GetType(), methodName, args);
        if (method == null)
        {
            return false;
        }

        try
        {
            method.Invoke(instance, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInvokeObjectMethodByNames(object instance, params string[] methodNames)
    {
        foreach (string methodName in methodNames)
        {
            if (TryInvokeObjectMethod(instance, methodName))
            {
                return true;
            }
        }

        return false;
    }
}
