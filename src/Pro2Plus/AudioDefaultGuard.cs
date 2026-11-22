using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Pro2Plus;

/// <summary>
/// 默认播放/录音设备的守护：若手柄的音频接口被系统设成默认端点，就把它迁移到
/// 另一台活跃设备。只改默认选择，不禁用设备，原生触觉所需的音频接口始终保留。
/// </summary>
public static class AudioDefaultGuard
{
    private const int ActiveDeviceState = 0x00000001;
    private const int ReadOnlyStore = 0x00000000;

    private static readonly AudioPropertyKey FriendlyNameKey = new(
        Guid.Parse("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly AudioPropertyKey InstanceIdKey = new(
        Guid.Parse("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);

    public static IReadOnlyList<string> EnsureControllerIsNotDefault()
    {
        var logs = new List<string>();
        object? enumeratorObject = null;
        object? policyObject = null;
        try
        {
            enumeratorObject = new EndpointEnumeratorComClass();
            var enumerator = (IAudioEndpointEnumerator)enumeratorObject;
            policyObject = new DefaultPolicyComClass();
            var policy = (IAudioDefaultPolicy)policyObject;

            InspectFlow(enumerator, policy, AudioFlow.Render, logs);
            InspectFlow(enumerator, policy, AudioFlow.Capture, logs);
            if (logs.Count == 0)
            {
                logs.Add(AudioEndpointPolicy.DescribeClean());
            }
        }
        catch (Exception ex)
        {
            logs.Add(AudioEndpointPolicy.DescribeWarning(ex));
        }
        finally
        {
            AudioComRuntime.Release(policyObject);
            AudioComRuntime.Release(enumeratorObject);
        }

        return logs;
    }

    /// <summary>
    /// 三个角色的默认端点只查一次，既供迁移判定也供日志与备选挑选；
    /// 旧实现每个流向要重复查六次 COM。
    /// </summary>
    private static void InspectFlow(
        IAudioEndpointEnumerator enumerator,
        IAudioDefaultPolicy policy,
        AudioFlow flow,
        List<string> logs)
    {
        List<EndpointSnapshot> active = EnumerateActive(enumerator, flow);
        if (active.Count == 0)
        {
            logs.Add(AudioEndpointPolicy.DescribeNoActive(flow));
            return;
        }

        var defaults = new EndpointSnapshot?[AudioEndpointPolicy.InspectionOrder.Length];
        foreach (EndpointRole role in AudioEndpointPolicy.InspectionOrder)
        {
            defaults[(int)role] = ReadDefaultEndpoint(enumerator, flow, role);
        }

        EndpointSnapshot? fallback = AudioEndpointPolicy.PickFallback(active, defaults);
        foreach (EndpointRole role in AudioEndpointPolicy.InspectionOrder)
        {
            EndpointSnapshot? current = defaults[(int)role];
            if (current == null)
            {
                continue;
            }

            logs.Add(AudioEndpointPolicy.DescribeCurrent(flow, role, current));
            if (!current.IsController)
            {
                continue;
            }

            if (fallback == null)
            {
                logs.Add(AudioEndpointPolicy.DescribeNoFallback(flow, role));
                continue;
            }

            int hr = policy.SetDefaultEndpoint(fallback.Id, role);
            logs.Add(hr == 0
                ? AudioEndpointPolicy.DescribeMoved(flow, role, fallback)
                : AudioEndpointPolicy.DescribeMoveFailure(flow, role, fallback, hr));
        }
    }

    private static List<EndpointSnapshot> EnumerateActive(
        IAudioEndpointEnumerator enumerator,
        AudioFlow flow)
    {
        IAudioEndpointCollection? collection = null;
        var endpoints = new List<EndpointSnapshot>();
        try
        {
            if (enumerator.EnumAudioEndpoints(flow, ActiveDeviceState, out collection) != 0 ||
                collection == null)
            {
                return endpoints;
            }

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                IAudioEndpointDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) == 0 && device != null)
                    {
                        endpoints.Add(ReadEndpoint(device, flow));
                    }
                }
                finally
                {
                    AudioComRuntime.Release(device);
                }
            }
        }
        finally
        {
            AudioComRuntime.Release(collection);
        }

        return endpoints;
    }

    private static EndpointSnapshot? ReadDefaultEndpoint(
        IAudioEndpointEnumerator enumerator,
        AudioFlow flow,
        EndpointRole role)
    {
        IAudioEndpointDevice? device = null;
        try
        {
            return enumerator.GetDefaultAudioEndpoint(flow, role, out device) == 0 && device != null
                ? ReadEndpoint(device, flow)
                : null;
        }
        finally
        {
            AudioComRuntime.Release(device);
        }
    }

    private static EndpointSnapshot ReadEndpoint(IAudioEndpointDevice device, AudioFlow flow)
    {
        string id = ReadDeviceId(device);
        (string name, string instance) = ReadProperties(device);
        bool controller = AudioEndpointPolicy.IsControllerEndpoint(id, name);
        if (!controller && !string.IsNullOrWhiteSpace(instance))
        {
            // USB 产品串只是 "Wireless Controller"，必须再查 PnP 祖先的硬件 ID。
            controller = ControllerAudioLineage.DescendsFromController(instance);
        }

        return new EndpointSnapshot(id, name, flow, controller);
    }

    private static string ReadDeviceId(IAudioEndpointDevice device)
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            return device.GetId(out pointer) == 0
                ? AudioComRuntime.ReadCoTaskString(pointer)
                : "";
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    /// <summary>属性包只打开一次，同时取友好名与设备实例 ID。</summary>
    private static (string Name, string Instance) ReadProperties(IAudioEndpointDevice device)
    {
        IAudioPropertyBag? store = null;
        AudioPropertyValue nameValue = default;
        AudioPropertyValue instanceValue = default;
        try
        {
            if (device.OpenPropertyStore(ReadOnlyStore, out store) != 0 || store == null)
            {
                return ("", "");
            }

            AudioPropertyKey nameKey = FriendlyNameKey;
            AudioPropertyKey instanceKey = InstanceIdKey;
            string name = store.GetValue(ref nameKey, out nameValue) == 0 ? nameValue.AsString() : "";
            string instance = store.GetValue(ref instanceKey, out instanceValue) == 0
                ? instanceValue.AsString()
                : "";
            return (name, instance);
        }
        finally
        {
            AudioPropertyValue.Clear(ref nameValue);
            AudioPropertyValue.Clear(ref instanceValue);
            AudioComRuntime.Release(store);
        }
    }
}
