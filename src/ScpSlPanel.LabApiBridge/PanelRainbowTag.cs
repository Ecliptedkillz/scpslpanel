using System;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace ScpSlPanel.LabApiBridge;

internal static class PanelRainbowTag
{
    public static void Attach(Player player)
    {
        if (player?.GameObject == null) return;
        var behaviour = player.GameObject.GetComponent<PanelRainbowTagBehaviour>()
            ?? player.GameObject.AddComponent<PanelRainbowTagBehaviour>();
        behaviour.Initialize();
    }
}

internal sealed class PanelRainbowTagBehaviour : MonoBehaviour
{
    private static readonly string[] Colors =
    {
        "red",
        "orange",
        "yellow",
        "green",
        "blue_green",
        "magenta",
    };

    private ServerRoles? _roles;
    private string _originalColor = string.Empty;
    private float _nextCycle;
    private int _position;

    public void Initialize()
    {
        _position = 0;
        _nextCycle = Time.time;
    }

    private void Awake()
    {
        _roles = GetComponent<ServerRoles>();
        if (_roles == null)
        {
            Logger.Warn("SCP Control could not attach its rainbow tag: ServerRoles was unavailable.");
            Destroy(this);
            return;
        }

        _originalColor = _roles.Network_myColor;
    }

    private void Update()
    {
        if (_roles == null || Time.time < _nextCycle) return;
        _nextCycle = Time.time + 0.5f;
        _roles.Network_myColor = Colors[_position];
        _position = (_position + 1) % Colors.Length;
    }

    private void OnDestroy()
    {
        if (_roles != null)
            _roles.Network_myColor = _originalColor;
    }
}
