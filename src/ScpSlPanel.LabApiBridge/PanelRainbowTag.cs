using System;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using UnityEngine;

namespace ScpSlPanel.LabApiBridge;

internal static class PanelRainbowTag
{
    public static void Attach(Player player, string badgeText)
    {
        if (player?.GameObject == null) return;
        var behaviour = player.GameObject.GetComponent<PanelRainbowTagBehaviour>()
            ?? player.GameObject.AddComponent<PanelRainbowTagBehaviour>();
        behaviour.Initialize(badgeText);
    }

    public static void Detach(Player player)
    {
        if (player?.GameObject == null) return;
        var behaviour = player.GameObject.GetComponent<PanelRainbowTagBehaviour>();
        behaviour?.Stop();
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
    private string _expectedText = string.Empty;
    private bool _restoreOriginal = true;

    public void Initialize(string badgeText)
    {
        _expectedText = badgeText ?? string.Empty;
        if (_roles != null)
            _originalColor = _roles.Network_myColor;
        _restoreOriginal = true;
        enabled = true;
        _position = 0;
        _nextCycle = Time.time;
    }

    public void Stop()
    {
        if (_roles != null && enabled && _restoreOriginal)
            _roles.Network_myColor = _originalColor;
        _restoreOriginal = false;
        enabled = false;
    }

    private void Awake()
    {
        _roles = GetComponent<ServerRoles>();
        if (_roles == null)
        {
            LabApi.Features.Console.Logger.Warn(
                "SCP Control could not attach its rainbow tag: ServerRoles was unavailable.");
            Destroy(this);
            return;
        }

        _originalColor = _roles.Network_myColor;
    }

    private void Update()
    {
        if (_roles == null || Time.time < _nextCycle) return;
        var currentText = Convert.ToString(_roles.GetType().GetProperty("Network_myText")?.GetValue(_roles, null));
        if (!string.IsNullOrWhiteSpace(_expectedText)
            && !string.Equals(currentText, _expectedText, StringComparison.Ordinal))
        {
            // Another tag provider or the player changed the displayed tag. Do not
            // restore the old color over the newly-selected tag when removing us.
            _restoreOriginal = false;
            enabled = false;
            return;
        }
        _nextCycle = Time.time + 0.5f;
        _roles.Network_myColor = Colors[_position];
        _position = (_position + 1) % Colors.Length;
    }

    private void OnDestroy()
    {
        if (_roles != null && _restoreOriginal)
            _roles.Network_myColor = _originalColor;
    }
}
