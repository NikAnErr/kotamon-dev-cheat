using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Attributes;
using Project.Code.Core.Player.Controllers;
using Project.Code.Core.Player.Movement;
using Project.Code.Gameplay.Controllers;
using Project.Code.Gameplay.Interactions.Pickups;
using Project.Code.Gameplay.Player;
using Project.Code.Gameplay.Player.Controllers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KotamonDevCheat;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "local.kotamon.devcheat";
    public const string PluginName = "Kotamon Dev Cheat";
    public const string PluginVersion = "0.2.5";

    internal static ManualLogSource ModLog { get; private set; }
    internal static ConfigFile ModConfig { get; private set; }

    internal static ConfigEntry<KeyCode> MenuKey { get; private set; }
    internal static ConfigEntry<KeyCode> NoclipKey { get; private set; }
    internal static ConfigEntry<KeyCode> WorldSpeedKey { get; private set; }
    internal static ConfigEntry<KeyCode> EspKey { get; private set; }
    internal static ConfigEntry<KeyCode> AutoCleanupKey { get; private set; }

    internal static ConfigEntry<float> NoclipSpeed { get; private set; }
    internal static ConfigEntry<float> WorldSpeedValue { get; private set; }
    internal static ConfigEntry<float> EspDistance { get; private set; }
    internal static ConfigEntry<int> MoneyTarget { get; private set; }

    internal static ConfigEntry<bool> WorldSpeedEnabled { get; private set; }
    internal static ConfigEntry<bool> EspEnabled { get; private set; }

    private CheatBehaviour _behaviour;

    public override void Load()
    {
        ModLog = Log;
        ModConfig = Config;

        MenuKey = Config.Bind("Hotkeys", "Menu", KeyCode.Insert, "Open or close the cheat menu.");
        NoclipKey = Config.Bind("Hotkeys", "Noclip", KeyCode.F1, "Toggle noclip.");
        WorldSpeedKey = Config.Bind("Hotkeys", "WorldSpeed", KeyCode.F2, "Toggle selected world speed.");
        EspKey = Config.Bind("Hotkeys", "ESP", KeyCode.F3, "Toggle junk and card ESP.");
        AutoCleanupKey = Config.Bind("Hotkeys", "AutoCleanup", KeyCode.F4, "Collect all cards, then delete all remaining junk.");

        NoclipSpeed = Config.Bind("Values", "NoclipSpeed", 10f, "Noclip movement speed.");
        WorldSpeedValue = Config.Bind("Values", "WorldSpeed", 2f, "Time.timeScale while WorldSpeed is enabled.");
        EspDistance = Config.Bind("Values", "EspDistance", 75f, "Maximum ESP distance in metres.");
        MoneyTarget = Config.Bind("Values", "MoneyTarget", 100000, "Exact money amount applied by the menu button.");

        WorldSpeedEnabled = Config.Bind("Toggles", "WorldSpeed", false, "Persist WorldSpeed state.");
        EspEnabled = Config.Bind("Toggles", "ESP", false, "Persist ESP state.");

        ClampConfiguration();
        _behaviour = AddComponent<CheatBehaviour>();
        Log.LogInfo($"Kotamon Dev Cheat {PluginVersion} loaded. {MenuKey.Value}=Menu, {NoclipKey.Value}=Noclip, " +
            $"{WorldSpeedKey.Value}=WorldSpeed, {EspKey.Value}=ESP, {AutoCleanupKey.Value}=Auto Cleanup.");
    }

    public override bool Unload()
    {
        if (_behaviour != null)
            _behaviour.Shutdown();

        if (_behaviour != null)
            Object.Destroy(_behaviour);

        _behaviour = null;
        return true;
    }

    internal static void SaveConfig()
    {
        try
        {
            ModConfig.Save();
        }
        catch (Exception exception)
        {
            ModLog.LogWarning($"Could not save config: {exception.Message}");
        }
    }

    private static void ClampConfiguration()
    {
        NoclipSpeed.Value = Mathf.Clamp(NoclipSpeed.Value, 1f, 50f);
        WorldSpeedValue.Value = Mathf.Clamp(WorldSpeedValue.Value, 0.1f, 5f);
        EspDistance.Value = Mathf.Clamp(EspDistance.Value, 10f, 200f);
        MoneyTarget.Value = Math.Max(0, Math.Min(999999999, MoneyTarget.Value));
        SaveConfig();
    }
}

public sealed class CheatBehaviour : MonoBehaviour
{
    private const float EspRefreshInterval = 0.4f;
    private const int EspMaxTargets = 96;

    private readonly List<JunkPickup> _espTargets = new();
    private readonly HashSet<int> _zoneCardInstanceIds = new();

    private PlayerNoClipController _nativeNoclip;
    private PlayerCharacterController _player;
    private PlayerMovementController _movement;
    private CharacterController _characterController;
    private PlayerCollectionController _collectionController;
    private PlayerPickupController _pickupController;
    private ParametersController _parametersController;
    private PlayerCameraController _playerCameraController;
    private Camera _camera;

    private bool _fallbackNoclip;
    private bool _movementWasEnabled;
    private bool _characterControllerWasEnabled;
    private bool _menuOpen;
    private bool _previousCursorVisible;
    private bool _cameraControllerWasEnabled;
    private bool _cameraControllerSuppressed;
    private bool _guiErrorLogged;
    private bool _glLineUnavailable;
    private CursorLockMode _previousCursorLock;
    private BindingAction _bindingAction;

    private float _nextEspRefresh;
    private float _nextFragmentRefresh;
    private float _nextAutomationErrorLog;
    private int _cleanupCardsRemaining;
    private int _cleanupTrashRemaining;
    private int _fragmentPartsCount;
    private int _fragmentPartsNeeded = 5;
    private int _lastCleanupFragmentsCollected;
    private int _lastMoneyValue = -1;
    private float _fragmentSecondsRemaining = -1f;
    private string _cleanupPhase = "Idle";

    private Material _lineMaterial;
    private Rect _menuRect = new(395f, 20f, 590f, 510f);
    private Rect _statusRect = new(15f, 15f, 375f, 175f);
    private DragTarget _dragTarget;
    private Vector2 _dragOffset;

    private enum BindingAction
    {
        None,
        Menu,
        Noclip,
        WorldSpeed,
        Esp,
        AutoCleanup
    }

    private enum DragTarget
    {
        None,
        Menu,
        Status
    }

    private enum CardTargetKind
    {
        None,
        DirtyCard,
        CardBox
    }

    public CheatBehaviour(IntPtr pointer) : base(pointer)
    {
    }

    public void Update()
    {
        if (_bindingAction == BindingAction.None && Input.GetKeyDown(Plugin.MenuKey.Value))
            SetMenuOpen(!_menuOpen);

        if (_menuOpen)
            MaintainMenuInputCapture();

        if (_bindingAction == BindingAction.None)
            ProcessHotkeys();

        ApplyNoclipSpeed();

        if (_fallbackNoclip)
            UpdateFallbackNoclip();

        if (Plugin.WorldSpeedEnabled.Value)
            Time.timeScale = Plugin.WorldSpeedValue.Value;

        if (Plugin.EspEnabled.Value && Time.realtimeSinceStartup >= _nextEspRefresh)
            RefreshEspTargets();

        if (Time.realtimeSinceStartup >= _nextFragmentRefresh)
            RefreshFragmentState();

    }

    public void LateUpdate()
    {
        if (_menuOpen)
            MaintainMenuInputCapture();
    }

    public void OnGUI()
    {
        try
        {
            GUI.depth = -1000;
            CaptureRebindEvent();

            if (Plugin.EspEnabled.Value)
                DrawEsp();

            if (_menuOpen)
                DrawMenu();

            DrawCompactStatus();
        }
        catch (Exception exception)
        {
            if (_guiErrorLogged)
                return;

            _guiErrorLogged = true;
            Plugin.ModLog.LogError($"Menu/ESP drawing failed: {exception}");
        }
    }

    [HideFromIl2Cpp]
    internal void Shutdown()
    {
        SetMenuOpen(false);

        if (_fallbackNoclip)
            SetFallbackNoclip(false);

        try
        {
            if (_nativeNoclip != null && _nativeNoclip.isNoclip)
                _nativeNoclip.Toggle();
        }
        catch (Exception exception)
        {
            Plugin.ModLog.LogWarning($"Could not disable native noclip during unload: {exception.Message}");
        }

        if (_lineMaterial != null)
            Object.Destroy(_lineMaterial);

        Time.timeScale = 1f;
    }

    [HideFromIl2Cpp]
    private void ProcessHotkeys()
    {
        if (Input.GetKeyDown(Plugin.NoclipKey.Value))
            ToggleNoclip();

        if (Input.GetKeyDown(Plugin.WorldSpeedKey.Value))
            SetWorldSpeedEnabled(!Plugin.WorldSpeedEnabled.Value);

        if (Input.GetKeyDown(Plugin.EspKey.Value))
            SetEspEnabled(!Plugin.EspEnabled.Value);

        if (Input.GetKeyDown(Plugin.AutoCleanupKey.Value))
            RunAutoCleanup();
    }

    [HideFromIl2Cpp]
    private void SetMenuOpen(bool open)
    {
        if (_menuOpen == open)
            return;

        _menuOpen = open;
        _bindingAction = BindingAction.None;

        if (open)
        {
            _previousCursorVisible = Cursor.visible;
            _previousCursorLock = Cursor.lockState;
            MaintainMenuInputCapture();
        }
        else
        {
            RestoreCameraController();
            Cursor.visible = _previousCursorVisible;
            Cursor.lockState = _previousCursorLock;
            _dragTarget = DragTarget.None;
        }
    }

    [HideFromIl2Cpp]
    private void MaintainMenuInputCapture()
    {
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        try
        {
            if (_cameraControllerSuppressed && _playerCameraController == null)
                _cameraControllerSuppressed = false;

            if (!_cameraControllerSuppressed)
            {
                _playerCameraController = Object.FindObjectOfType<PlayerCameraController>();
                if (_playerCameraController != null)
                {
                    _cameraControllerWasEnabled = _playerCameraController.enabled;
                    _playerCameraController.enabled = false;
                    _cameraControllerSuppressed = true;
                }
            }
            else if (_playerCameraController.enabled)
            {
                _playerCameraController.enabled = false;
            }
        }
        catch (Exception exception)
        {
            LogAutomationError("Menu input capture", exception);
        }
    }

    [HideFromIl2Cpp]
    private void RestoreCameraController()
    {
        try
        {
            if (_cameraControllerSuppressed && _playerCameraController != null)
                _playerCameraController.enabled = _cameraControllerWasEnabled;
        }
        catch (Exception exception)
        {
            Plugin.ModLog.LogWarning($"Could not restore player camera: {exception.Message}");
        }

        _cameraControllerSuppressed = false;
        _playerCameraController = null;
    }

    [HideFromIl2Cpp]
    private void ToggleNoclip()
    {
        try
        {
            _nativeNoclip = Object.FindObjectOfType<PlayerNoClipController>();
            if (_nativeNoclip != null)
            {
                if (_fallbackNoclip)
                    SetFallbackNoclip(false);

                _nativeNoclip.moveSpeed = Plugin.NoclipSpeed.Value;
                _nativeNoclip.Toggle();
                Plugin.ModLog.LogInfo($"Noclip: {(_nativeNoclip.isNoclip ? "ON" : "OFF")}, speed={Plugin.NoclipSpeed.Value:0.0}");
                return;
            }
        }
        catch (Exception exception)
        {
            Plugin.ModLog.LogWarning($"Native noclip failed, using fallback: {exception.Message}");
        }

        SetFallbackNoclip(!_fallbackNoclip);
    }

    [HideFromIl2Cpp]
    private bool IsNoclipEnabled()
    {
        try
        {
            return (_nativeNoclip != null && _nativeNoclip.isNoclip) || _fallbackNoclip;
        }
        catch
        {
            _nativeNoclip = null;
            return _fallbackNoclip;
        }
    }

    [HideFromIl2Cpp]
    private void ApplyNoclipSpeed()
    {
        try
        {
            if (_nativeNoclip != null)
                _nativeNoclip.moveSpeed = Plugin.NoclipSpeed.Value;
        }
        catch
        {
            _nativeNoclip = null;
        }
    }

    [HideFromIl2Cpp]
    private void SetFallbackNoclip(bool enabled)
    {
        if (enabled)
        {
            _player = Object.FindObjectOfType<PlayerCharacterController>();
            if (_player == null)
            {
                Plugin.ModLog.LogWarning("Noclip: PlayerCharacterController was not found.");
                return;
            }

            _movement = _player.GetComponent<PlayerMovementController>();
            _characterController = _player.GetComponent<CharacterController>();

            if (_movement != null)
            {
                _movementWasEnabled = _movement.enabled;
                _movement.enabled = false;
            }

            if (_characterController != null)
            {
                _characterControllerWasEnabled = _characterController.enabled;
                _characterController.enabled = false;
            }

            _fallbackNoclip = true;
        }
        else
        {
            if (_movement != null)
                _movement.enabled = _movementWasEnabled;

            if (_characterController != null)
                _characterController.enabled = _characterControllerWasEnabled;

            _fallbackNoclip = false;
        }

        Plugin.ModLog.LogInfo($"Noclip fallback: {(_fallbackNoclip ? "ON" : "OFF")}");
    }

    [HideFromIl2Cpp]
    private void UpdateFallbackNoclip()
    {
        if (_player == null)
        {
            SetFallbackNoclip(false);
            return;
        }

        _camera = Camera.main;
        if (_camera == null)
            return;

        var direction = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) direction += _camera.transform.forward;
        if (Input.GetKey(KeyCode.S)) direction -= _camera.transform.forward;
        if (Input.GetKey(KeyCode.D)) direction += _camera.transform.right;
        if (Input.GetKey(KeyCode.A)) direction -= _camera.transform.right;
        if (Input.GetKey(KeyCode.Space)) direction += Vector3.up;
        if (Input.GetKey(KeyCode.LeftControl)) direction -= Vector3.up;

        if (direction.sqrMagnitude < 0.0001f)
            return;

        var boost = Input.GetKey(KeyCode.LeftShift) ? 3f : 1f;
        _player.transform.position += direction.normalized * Plugin.NoclipSpeed.Value * boost * Time.unscaledDeltaTime;
    }

    [HideFromIl2Cpp]
    private void SetWorldSpeedEnabled(bool enabled)
    {
        Plugin.WorldSpeedEnabled.Value = enabled;
        Time.timeScale = enabled ? Plugin.WorldSpeedValue.Value : 1f;
        Plugin.SaveConfig();
        Plugin.ModLog.LogInfo($"WorldSpeed: {(enabled ? $"ON ({Plugin.WorldSpeedValue.Value:0.00}x)" : "OFF")}");
    }

    [HideFromIl2Cpp]
    private void SetEspEnabled(bool enabled)
    {
        Plugin.EspEnabled.Value = enabled;
        _nextEspRefresh = 0f;
        if (!enabled)
            _espTargets.Clear();
        Plugin.SaveConfig();
        Plugin.ModLog.LogInfo($"ESP: {(enabled ? "ON" : "OFF")}");
    }

    [HideFromIl2Cpp]
    private void PrepareAutoCleanup()
    {
        _cleanupPhase = "Scanning";
        _lastCleanupFragmentsCollected = 0;
    }

    [HideFromIl2Cpp]
    private void ApplyMoneyTarget()
    {
        try
        {
            _parametersController = Object.FindObjectOfType<ParametersController>();
            if (_parametersController == null)
            {
                Plugin.ModLog.LogWarning("Set Money failed: ParametersController was not found.");
                return;
            }

            Plugin.MoneyTarget.Value = Math.Max(0, Math.Min(999999999, Plugin.MoneyTarget.Value));
            _parametersController.SetParameter(ParameterType.Money, Plugin.MoneyTarget.Value);
            _parametersController.Save();
            _lastMoneyValue = _parametersController.GetValue(ParameterType.Money);
            Plugin.SaveConfig();
            Plugin.ModLog.LogInfo($"Money set to {_lastMoneyValue}.");
        }
        catch (Exception exception)
        {
            LogAutomationError("Set Money", exception);
        }
    }

    [HideFromIl2Cpp]
    private void RefreshFragmentState()
    {
        _nextFragmentRefresh = Time.realtimeSinceStartup + 0.25f;

        try
        {
            if (_parametersController == null)
                _parametersController = Object.FindObjectOfType<ParametersController>();
            if (_pickupController == null)
                _pickupController = Object.FindObjectOfType<PlayerPickupController>();
            if (_collectionController == null)
                _collectionController = Object.FindObjectOfType<PlayerCollectionController>();

            if (_parametersController != null)
                _fragmentPartsCount = _parametersController.GetValue(ParameterType.DirtyPartsCount);

            var dirtyPart = _collectionController == null || _collectionController._cardsSettings == null
                ? null
                : _collectionController._cardsSettings.DirtyPart;
            if (dirtyPart != null && dirtyPart.NeedCount > 0)
                _fragmentPartsNeeded = dirtyPart.NeedCount;

            _fragmentSecondsRemaining = _pickupController == null
                ? -1f
                : Mathf.Max(0f, (float)_pickupController._needPartTimer - (float)_pickupController._takingTimer);
        }
        catch (Exception exception)
        {
            LogAutomationError("Fragment state", exception);
        }
    }

    [HideFromIl2Cpp]
    private int CollectInstantFragment()
    {
        RefreshFragmentState();
        if (_parametersController == null)
            throw new InvalidOperationException("ParametersController was not found.");

        if (_fragmentPartsCount >= _fragmentPartsNeeded)
            return 0;

        var newCount = Math.Min(_fragmentPartsNeeded, _fragmentPartsCount + 1);
        _parametersController.SetParameter(ParameterType.DirtyPartsCount, newCount);
        _parametersController.Save();

        // This is the same reset performed by the native fragment timer after it
        // grants a part. It prevents the old timer from granting a duplicate.
        if (_pickupController != null)
            _pickupController.UpdatePartTimer();

        _fragmentPartsCount = newCount;
        _fragmentSecondsRemaining = _pickupController == null
            ? -1f
            : Mathf.Max(0f, (float)_pickupController._needPartTimer - (float)_pickupController._takingTimer);
        return 1;
    }

    [HideFromIl2Cpp]
    private void RefreshEspTargets()
    {
        _nextEspRefresh = Time.realtimeSinceStartup + EspRefreshInterval;
        _espTargets.Clear();
        RefreshZoneCardIds();

        try
        {
            var found = Object.FindObjectsByType<JunkPickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (var index = 0; index < found.Length; index++)
            {
                var target = found[index];
                if (target == null)
                    continue;

                var kind = ClassifyCardTarget(target);
                if (kind == CardTargetKind.DirtyCard)
                    _espTargets.Add(target);
            }

            _camera = Camera.main;
            if (_camera != null)
            {
                var cameraPosition = _camera.transform.position;
                _espTargets.Sort((left, right) =>
                    GetSafeDistanceSquared(left, cameraPosition).CompareTo(GetSafeDistanceSquared(right, cameraPosition)));
            }
        }
        catch (Exception exception)
        {
            LogAutomationError("ESP refresh", exception);
        }
    }

    [HideFromIl2Cpp]
    private static float GetSafeDistanceSquared(JunkPickup target, Vector3 origin)
    {
        try
        {
            return target == null ? float.MaxValue : Vector3.SqrMagnitude(target.transform.position - origin);
        }
        catch
        {
            return float.MaxValue;
        }
    }

    [HideFromIl2Cpp]
    private void DrawEsp()
    {
        _camera = Camera.main;
        if (_camera == null)
            return;

        var origin = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var drawn = 0;

        DrawFragmentEspStatus();

        for (var index = 0; index < _espTargets.Count && drawn < EspMaxTargets; index++)
        {
            var target = _espTargets[index];
            if (target == null || !target.isActiveAndEnabled)
                continue;

            try
            {
                if (!TryGetScreenRect(target, _camera, out var rect, out var distance))
                    continue;

                if (distance > Plugin.EspDistance.Value)
                    continue;

                var kind = ClassifyCardTarget(target);
                if (kind != CardTargetKind.DirtyCard)
                    continue;

                var color = GetEspColor(kind);
                DrawTargetBox(rect, color);
                DrawThinLine(origin, rect.center, color);

                var previousColor = GUI.color;
                GUI.color = color;
                GUI.Label(new Rect(rect.x, Math.Max(0f, rect.y - 20f), Math.Max(180f, rect.width + 90f), 20f),
                    GetTargetLabel(distance, kind));
                GUI.color = previousColor;
                drawn++;
            }
            catch
            {
                // Pooled pickups can become invalid between refresh and OnGUI.
            }
        }
    }

    [HideFromIl2Cpp]
    private void DrawFragmentEspStatus()
    {
        var timer = _fragmentSecondsRemaining >= 0f
            ? $"next in {_fragmentSecondsRemaining:0}s"
            : "timer unavailable";
        var text = $"CARD FRAGMENTS  {_fragmentPartsCount}/{_fragmentPartsNeeded}  |  {timer}";
        var rect = new Rect(Math.Max(0f, Screen.width * 0.5f - 165f), 12f, 330f, 24f);
        var previousColor = GUI.color;
        GUI.color = new Color(0.1f, 0.95f, 1f, 1f);
        GUI.Box(rect, text);
        GUI.color = previousColor;
    }

    [HideFromIl2Cpp]
    private static void DrawTargetBox(Rect rect, Color color)
    {
        var previousColor = GUI.color;
        GUI.color = color;
        GUI.Box(rect, string.Empty);
        GUI.color = previousColor;
    }

    [HideFromIl2Cpp]
    private void DrawThinLine(Vector2 from, Vector2 to, Color color)
    {
        if (!_glLineUnavailable && Event.current != null && Event.current.type == EventType.Repaint)
        {
            try
            {
                if (_lineMaterial == null)
                {
                    var shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader == null)
                        throw new InvalidOperationException("Hidden/Internal-Colored shader was not found.");

                    _lineMaterial = new Material(shader);
                    _lineMaterial.hideFlags = HideFlags.HideAndDontSave;
                }

                if (_lineMaterial.SetPass(0))
                {
                    GL.PushMatrix();
                    GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);
                    GL.Begin(GL.LINES);
                    GL.Color(color);
                    GL.Vertex3(from.x, from.y, 0f);
                    GL.Vertex3(to.x, to.y, 0f);
                    GL.End();
                    GL.PopMatrix();
                    return;
                }
            }
            catch (Exception exception)
            {
                _glLineUnavailable = true;
                Plugin.ModLog.LogWarning($"GL ESP lines unavailable, using dotted fallback: {exception.Message}");
            }
        }

        if (_glLineUnavailable)
            DrawDottedFallback(from, to, color);
    }

    [HideFromIl2Cpp]
    private static void DrawDottedFallback(Vector2 from, Vector2 to, Color color)
    {
        var difference = to - from;
        var length = difference.magnitude;
        if (length < 1f)
            return;

        var steps = Mathf.Clamp((int)(length / 8f), 8, 128);
        var previousColor = GUI.color;
        GUI.color = color;

        for (var index = 0; index <= steps; index++)
        {
            var point = from + difference * (index / (float)steps);
            GUI.Label(new Rect(point.x - 4f, point.y - 9f, 10f, 18f), "*");
        }

        GUI.color = previousColor;
    }

    [HideFromIl2Cpp]
    private static bool TryGetScreenRect(JunkPickup target, Camera camera, out Rect rect, out float distance)
    {
        rect = default;
        var center = target.transform.position;
        distance = Vector3.Distance(camera.transform.position, center);

        var renderer = target.GetComponentInChildren<Renderer>();
        var bounds = renderer != null ? renderer.bounds : new Bounds(center, new Vector3(0.6f, 0.6f, 0.6f));
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;
        var visibleCorners = 0;

        for (var index = 0; index < 8; index++)
        {
            var corner = bounds.center + new Vector3(
                (index & 1) == 0 ? -bounds.extents.x : bounds.extents.x,
                (index & 2) == 0 ? -bounds.extents.y : bounds.extents.y,
                (index & 4) == 0 ? -bounds.extents.z : bounds.extents.z);

            var screen = camera.WorldToScreenPoint(corner);
            if (screen.z <= 0f)
                continue;

            var guiY = Screen.height - screen.y;
            minX = Math.Min(minX, screen.x);
            minY = Math.Min(minY, guiY);
            maxX = Math.Max(maxX, screen.x);
            maxY = Math.Max(maxY, guiY);
            visibleCorners++;
        }

        if (visibleCorners == 0)
            return false;

        minX = Mathf.Clamp(minX, 0f, Screen.width);
        maxX = Mathf.Clamp(maxX, 0f, Screen.width);
        minY = Mathf.Clamp(minY, 0f, Screen.height);
        maxY = Mathf.Clamp(maxY, 0f, Screen.height);

        if (maxX - minX < 3f || maxY - minY < 3f)
            return false;

        rect = new Rect(minX, minY, maxX - minX, maxY - minY);
        return true;
    }

    [HideFromIl2Cpp]
    private static Color GetEspColor(CardTargetKind kind)
    {
        if (kind == CardTargetKind.DirtyCard)
            return new Color(1f, 0.25f, 1f, 1f);
        return Color.yellow;
    }

    [HideFromIl2Cpp]
    private static string GetTargetLabel(float distance, CardTargetKind kind)
    {
        if (kind == CardTargetKind.DirtyCard)
            return $"Dirty Card  {distance:0.0}m";
        return $"Unknown  {distance:0.0}m";
    }

    [HideFromIl2Cpp]
    private void RunAutoCleanup()
    {
        PrepareAutoCleanup();

        try
        {
            RefreshZoneCardIds();
            var pickups = Object.FindObjectsByType<JunkPickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var dirtyCards = new List<JunkPickup>();
            var cardBoxes = new List<JunkPickup>();
            var trash = new List<JunkPickup>();

            for (var index = 0; index < pickups.Length; index++)
            {
                var pickup = pickups[index];
                if (pickup == null || !pickup.isActiveAndEnabled)
                    continue;

                switch (ClassifyCardTarget(pickup))
                {
                    case CardTargetKind.DirtyCard:
                        dirtyCards.Add(pickup);
                        break;
                    case CardTargetKind.CardBox:
                        cardBoxes.Add(pickup);
                        break;
                    default:
                        trash.Add(pickup);
                        break;
                }
            }

            _cleanupCardsRemaining = dirtyCards.Count + cardBoxes.Count;
            _cleanupTrashRemaining = trash.Count;

            _collectionController = Object.FindObjectOfType<PlayerCollectionController>();
            if (_cleanupCardsRemaining > 0 && _collectionController == null)
            {
                _cleanupPhase = "No collection controller";
                Plugin.ModLog.LogWarning("Auto Cleanup aborted before trash removal: PlayerCollectionController was not found.");
                return;
            }

            _cleanupPhase = "Cards";
            var cardsCollected = 0;
            var boxesCollected = 0;
            var cardFailure = false;
            for (var index = 0; index < dirtyCards.Count; index++)
            {
                var pickup = dirtyCards[index];
                if (pickup == null || !pickup.isActiveAndEnabled)
                    continue;

                try
                {
                    _collectionController.TakeRandomCard();
                    RemoveWorldPickup(pickup);
                    cardsCollected++;
                }
                catch (Exception exception)
                {
                    cardFailure = true;
                    Plugin.ModLog.LogWarning($"Instant card collection failed for {DescribePickup(pickup)}: {exception.Message}");
                }
            }

            for (var index = 0; index < cardBoxes.Count; index++)
            {
                var pickup = cardBoxes[index];
                if (pickup == null || !pickup.isActiveAndEnabled)
                    continue;

                try
                {
                    _collectionController.TakeRandomCard();
                    RemoveWorldPickup(pickup);
                    boxesCollected++;
                }
                catch (Exception exception)
                {
                    cardFailure = true;
                    Plugin.ModLog.LogWarning($"Instant CardBox collection failed for {DescribePickup(pickup)}: {exception.Message}");
                }
            }

            if (cardFailure)
            {
                _cleanupPhase = "Card error; trash kept";
                _cleanupCardsRemaining = Math.Max(0, _cleanupCardsRemaining - cardsCollected - boxesCollected);
                return;
            }

            if (cardsCollected + boxesCollected > 0)
                _collectionController.Save();

            _cleanupPhase = "Fragment";
            if (dirtyCards.Count + cardBoxes.Count + trash.Count > 0)
            {
                try
                {
                    _lastCleanupFragmentsCollected = CollectInstantFragment();
                }
                catch (Exception exception)
                {
                    _cleanupPhase = "Fragment error; trash kept";
                    Plugin.ModLog.LogWarning($"Instant card fragment collection failed: {exception.Message}");
                    return;
                }
            }

            _cleanupPhase = "Trash";
            var trashRemoved = 0;
            for (var index = 0; index < trash.Count; index++)
            {
                var pickup = trash[index];
                if (pickup == null || !pickup.isActiveAndEnabled)
                    continue;

                RemoveWorldPickup(pickup);
                trashRemoved++;
            }

            _cleanupCardsRemaining = 0;
            _cleanupTrashRemaining = 0;
            _cleanupPhase = "Done";
            Plugin.ModLog.LogInfo($"Auto Cleanup completed in one pass: dirtyCards={cardsCollected}, cardBoxes={boxesCollected}, " +
                $"fragmentParts={_lastCleanupFragmentsCollected}, trash={trashRemoved}.");
        }
        catch (Exception exception)
        {
            _cleanupPhase = "Error";
            LogAutomationError("Auto Cleanup", exception);
        }
    }

    [HideFromIl2Cpp]
    private static void RemoveWorldPickup(JunkPickup pickup)
    {
        if (pickup == null)
            return;

        try
        {
            pickup.Destroyed();
        }
        catch
        {
            // Explicit object destruction below guarantees removal.
        }

        try
        {
            if (pickup != null && pickup.gameObject != null)
                Object.Destroy(pickup.gameObject);
        }
        catch
        {
            // The game may already have released the pooled object.
        }
    }

    [HideFromIl2Cpp]
    private void RefreshZoneCardIds()
    {
        _zoneCardInstanceIds.Clear();

        try
        {
            var zones = Object.FindObjectsByType<JunkZoneController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (var zoneIndex = 0; zoneIndex < zones.Length; zoneIndex++)
            {
                var zone = zones[zoneIndex];
                if (zone == null)
                    continue;

                try
                {
                    var cards = zone._cardPickups;
                    if (cards == null)
                        continue;

                    for (var cardIndex = 0; cardIndex < cards.Count; cardIndex++)
                    {
                        var card = cards[cardIndex];
                        if (card != null)
                            _zoneCardInstanceIds.Add(card.GetInstanceID());
                    }
                }
                catch
                {
                    // Name/data classification remains available as a fallback.
                }
            }
        }
        catch (Exception exception)
        {
            LogAutomationError("Card registry scan", exception);
        }
    }

    [HideFromIl2Cpp]
    private CardTargetKind ClassifyCardTarget(JunkPickup pickup)
    {
        if (pickup == null)
            return CardTargetKind.None;

        var trackedAsCard = false;
        var isCardBox = false;
        var isDirtyCard = false;
        var junkType = EJunkType.Common;
        var hasJunkType = false;

        try
        {
            trackedAsCard = _zoneCardInstanceIds.Contains(pickup.GetInstanceID());
        }
        catch
        {
            // Continue with data and name markers.
        }

        try
        {
            var data = pickup.Data;
            if (data != null)
            {
                junkType = data.JunkType;
                hasJunkType = true;
                ReadCardMarkers(data.Name, ref isCardBox, ref isDirtyCard);
                ReadCardMarkers(data.name, ref isCardBox, ref isDirtyCard);
                ReadCardMarkers(data.Description, ref isCardBox, ref isDirtyCard);
            }
        }
        catch
        {
            // Continue with object names and the zone registry.
        }

        try
        {
            ReadCardMarkers(pickup.name, ref isCardBox, ref isDirtyCard);
            ReadCardMarkers(pickup.gameObject.name, ref isCardBox, ref isDirtyCard);
        }
        catch
        {
            // Classification from data and the zone registry is still valid.
        }

        if ((hasJunkType && junkType == EJunkType.CardBox) || isCardBox)
            return CardTargetKind.CardBox;

        // Card fragments are a virtual DirtyPartsCount parameter and never have a
        // JunkPickup. Every active Card pickup in this build is a dirty card.
        if (trackedAsCard || isDirtyCard || (hasJunkType && junkType == EJunkType.Card))
            return CardTargetKind.DirtyCard;

        return CardTargetKind.None;
    }

    [HideFromIl2Cpp]
    private static void ReadCardMarkers(string value, ref bool isCardBox, ref bool isDirtyCard)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = value.ToLowerInvariant()
            .Replace(" ", string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty);

        if (normalized.Contains("cardbox") || normalized.Contains("boxcard"))
        {
            isCardBox = true;
            return;
        }

        if (normalized.Contains("carddirty") ||
            normalized.Contains("carddirt") ||
            normalized.Contains("dirtycard") ||
            normalized.Contains("dirtcard"))
            isDirtyCard = true;
    }

    [HideFromIl2Cpp]
    private void LogPickupDiagnostics(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<JunkPickup> pickups)
    {
        var limit = Math.Min(pickups.Length, 48);
        Plugin.ModLog.LogInfo($"Auto Cleanup scan: pickups={pickups.Length}, zoneCardIds={_zoneCardInstanceIds.Count}.");

        for (var index = 0; index < limit; index++)
        {
            var pickup = pickups[index];
            if (pickup == null)
                continue;

            var kind = ClassifyCardTarget(pickup);
            Plugin.ModLog.LogInfo($"Cleanup candidate [{index}]: kind={kind}, {DescribePickup(pickup)}");
        }
    }

    [HideFromIl2Cpp]
    private static string DescribePickup(JunkPickup pickup)
    {
        if (pickup == null)
            return "<destroyed>";

        var objectName = "?";
        var dataName = "?";
        var junkType = "?";

        try { objectName = pickup.name; } catch { }
        try { dataName = pickup.Data == null ? "<null>" : pickup.Data.Name; } catch { }
        try { junkType = pickup.Data == null ? "<null>" : pickup.Data.JunkType.ToString(); } catch { }
        return $"object='{objectName}', data='{dataName}', junkType={junkType}";
    }

    [HideFromIl2Cpp]
    private void LogAutomationError(string feature, Exception exception)
    {
        if (Time.realtimeSinceStartup < _nextAutomationErrorLog)
            return;

        _nextAutomationErrorLog = Time.realtimeSinceStartup + 5f;
        Plugin.ModLog.LogWarning($"{feature} failed: {exception.Message}");
    }

    [HideFromIl2Cpp]
    private void CaptureRebindEvent()
    {
        if (_bindingAction == BindingAction.None || Event.current == null)
            return;

        KeyCode key;
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode != KeyCode.None)
        {
            key = Event.current.keyCode;
            if (key == KeyCode.Escape)
            {
                _bindingAction = BindingAction.None;
                return;
            }
        }
        else if (Event.current.type == EventType.MouseDown && Event.current.button >= 0 && Event.current.button <= 6)
        {
            key = (KeyCode)((int)KeyCode.Mouse0 + Event.current.button);
        }
        else
        {
            return;
        }

        SetBinding(_bindingAction, key);
        _bindingAction = BindingAction.None;
        Plugin.SaveConfig();
    }

    [HideFromIl2Cpp]
    private static void SetBinding(BindingAction action, KeyCode key)
    {
        switch (action)
        {
            case BindingAction.Menu: Plugin.MenuKey.Value = key; break;
            case BindingAction.Noclip: Plugin.NoclipKey.Value = key; break;
            case BindingAction.WorldSpeed: Plugin.WorldSpeedKey.Value = key; break;
            case BindingAction.Esp: Plugin.EspKey.Value = key; break;
            case BindingAction.AutoCleanup: Plugin.AutoCleanupKey.Value = key; break;
        }
    }

    [HideFromIl2Cpp]
    private void DrawMenu()
    {
        var x = _menuRect.x;
        var y = _menuRect.y;
        var width = _menuRect.width;
        var height = _menuRect.height;

        HandleWindowDrag(ref _menuRect, new Rect(x, y, width, 25f), DragTarget.Menu);
        x = _menuRect.x;
        y = _menuRect.y;

        GUI.Box(new Rect(x, y, width, height), $"Kotamon Dev Cheat v{Plugin.PluginVersion}");
        GUI.Label(new Rect(x + 20f, y + 28f, width - 40f, 20f), $"Drag title. Click Key to rebind. {Plugin.MenuKey.Value} closes menu.");

        DrawFeatureRow(x, y + 58f, "Noclip", IsNoclipEnabled(), Plugin.NoclipKey.Value, BindingAction.Noclip, ToggleNoclip);
        var noclipSpeed = DrawValueRow(x, y + 92f, "Speed", Plugin.NoclipSpeed.Value, 1f, 50f, 1f, "0");
        if (Math.Abs(noclipSpeed - Plugin.NoclipSpeed.Value) > 0.001f)
        {
            Plugin.NoclipSpeed.Value = noclipSpeed;
            ApplyNoclipSpeed();
            Plugin.SaveConfig();
        }

        DrawFeatureRow(x, y + 135f, "WorldSpeed", Plugin.WorldSpeedEnabled.Value, Plugin.WorldSpeedKey.Value, BindingAction.WorldSpeed,
            () => SetWorldSpeedEnabled(!Plugin.WorldSpeedEnabled.Value));
        var worldSpeed = DrawValueRow(x, y + 169f, "Multiplier", Plugin.WorldSpeedValue.Value, 0.1f, 5f, 0.25f, "0.00x");
        if (Math.Abs(worldSpeed - Plugin.WorldSpeedValue.Value) > 0.001f)
        {
            Plugin.WorldSpeedValue.Value = worldSpeed;
            if (Plugin.WorldSpeedEnabled.Value)
                Time.timeScale = worldSpeed;
            Plugin.SaveConfig();
        }

        DrawFeatureRow(x, y + 212f, "ESP boxes + lines", Plugin.EspEnabled.Value, Plugin.EspKey.Value, BindingAction.Esp,
            () => SetEspEnabled(!Plugin.EspEnabled.Value));
        var espDistance = DrawValueRow(x, y + 246f, "Distance", Plugin.EspDistance.Value, 10f, 200f, 5f, "0m");
        if (Math.Abs(espDistance - Plugin.EspDistance.Value) > 0.001f)
        {
            Plugin.EspDistance.Value = espDistance;
            Plugin.SaveConfig();
        }

        if (GUI.Button(new Rect(x + 20f, y + 289f, 285f, 28f), "Auto Cleanup: RUN NOW"))
            RunAutoCleanup();

        if (GUI.Button(new Rect(x + 320f, y + 289f, 245f, 28f), BindingText(BindingAction.AutoCleanup, Plugin.AutoCleanupKey.Value)))
            _bindingAction = BindingAction.AutoCleanup;

        GUI.Label(new Rect(x + 20f, y + 336f, 140f, 25f), $"Money: {Plugin.MoneyTarget.Value}");
        if (GUI.Button(new Rect(x + 160f, y + 330f, 68f, 28f), "-10000"))
            Plugin.MoneyTarget.Value = Math.Max(0, Plugin.MoneyTarget.Value - 10000);
        if (GUI.Button(new Rect(x + 232f, y + 330f, 68f, 28f), "-1000"))
            Plugin.MoneyTarget.Value = Math.Max(0, Plugin.MoneyTarget.Value - 1000);
        if (GUI.Button(new Rect(x + 304f, y + 330f, 68f, 28f), "+1000"))
            Plugin.MoneyTarget.Value = Math.Min(999999999, Plugin.MoneyTarget.Value + 1000);
        if (GUI.Button(new Rect(x + 376f, y + 330f, 76f, 28f), "+10000"))
            Plugin.MoneyTarget.Value = Math.Min(999999999, Plugin.MoneyTarget.Value + 10000);
        if (GUI.Button(new Rect(x + 456f, y + 330f, 109f, 28f), "APPLY MONEY"))
            ApplyMoneyTarget();

        GUI.Label(new Rect(x + 20f, y + 378f, 170f, 25f), "Menu hotkey");
        if (GUI.Button(new Rect(x + 195f, y + 375f, 180f, 28f), BindingText(BindingAction.Menu, Plugin.MenuKey.Value)))
            _bindingAction = BindingAction.Menu;

        if (GUI.Button(new Rect(x + 390f, y + 375f, 165f, 28f), "Reset TimeScale"))
            SetWorldSpeedEnabled(false);

        GUI.Label(new Rect(x + 20f, y + 425f, width - 40f, 65f),
            "Auto Cleanup completes in one frame: cards -> +1 timed fragment -> trash.\n" +
            "Fragments are virtual: ESP shows their counter/timer; boxes mark dirty cards only.");
    }

    [HideFromIl2Cpp]
    private void DrawFeatureRow(float x, float y, string name, bool enabled, KeyCode key, BindingAction action, Action toggle)
    {
        if (GUI.Button(new Rect(x + 20f, y, 285f, 28f), $"{name}: {(enabled ? "ON" : "OFF")}"))
            toggle();

        if (GUI.Button(new Rect(x + 320f, y, 245f, 28f), BindingText(action, key)))
            _bindingAction = action;
    }

    [HideFromIl2Cpp]
    private static float DrawValueRow(float x, float y, string label, float value, float minimum, float maximum, float step, string format)
    {
        GUI.Label(new Rect(x + 40f, y, 190f, 25f), $"{label}: {value.ToString(format)}");

        if (GUI.Button(new Rect(x + 320f, y - 2f, 70f, 26f), $"-{step:0.##}"))
            value = Mathf.Clamp(value - step, minimum, maximum);

        if (GUI.Button(new Rect(x + 405f, y - 2f, 70f, 26f), $"+{step:0.##}"))
            value = Mathf.Clamp(value + step, minimum, maximum);

        if (GUI.Button(new Rect(x + 490f, y - 2f, 75f, 26f), "Reset"))
            value = DefaultValueFor(label);

        return value;
    }

    [HideFromIl2Cpp]
    private static float DefaultValueFor(string label)
    {
        return label switch
        {
            "Speed" => 10f,
            "Multiplier" => 2f,
            "Distance" => 75f,
            _ => 1f
        };
    }

    [HideFromIl2Cpp]
    private string BindingText(BindingAction action, KeyCode key)
    {
        return _bindingAction == action ? "PRESS KEY (Esc cancels)" : $"Key: {key}";
    }

    [HideFromIl2Cpp]
    private void HandleWindowDrag(ref Rect window, Rect titleBar, DragTarget target)
    {
        if (!_menuOpen || Event.current == null)
            return;

        var currentEvent = Event.current;
        if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && titleBar.Contains(currentEvent.mousePosition))
        {
            _dragTarget = target;
            _dragOffset = currentEvent.mousePosition - new Vector2(window.x, window.y);
            currentEvent.Use();
        }
        else if (currentEvent.type == EventType.MouseDrag && currentEvent.button == 0 && _dragTarget == target)
        {
            window.x = Mathf.Clamp(currentEvent.mousePosition.x - _dragOffset.x, 0f, Math.Max(0f, Screen.width - window.width));
            window.y = Mathf.Clamp(currentEvent.mousePosition.y - _dragOffset.y, 0f, Math.Max(0f, Screen.height - window.height));
            currentEvent.Use();
        }
        else if (currentEvent.rawType == EventType.MouseUp && _dragTarget == target)
        {
            _dragTarget = DragTarget.None;
        }
    }

    [HideFromIl2Cpp]
    private void DrawCompactStatus()
    {
        var x = _statusRect.x;
        var y = _statusRect.y;
        var width = _statusRect.width;

        HandleWindowDrag(ref _statusRect, new Rect(x, y, width, 25f), DragTarget.Status);
        x = _statusRect.x;
        y = _statusRect.y;

        GUI.Box(new Rect(x, y, width, _statusRect.height), $"Kotamon Dev Cheat [{Plugin.MenuKey.Value}]");
        GUI.Label(new Rect(x + 13f, y + 27f, 345f, 20f), $"{Plugin.NoclipKey.Value} Noclip: {(IsNoclipEnabled() ? "ON" : "OFF")}  speed {Plugin.NoclipSpeed.Value:0}");
        GUI.Label(new Rect(x + 13f, y + 49f, 345f, 20f), $"{Plugin.WorldSpeedKey.Value} WorldSpeed: {(Plugin.WorldSpeedEnabled.Value ? $"{Plugin.WorldSpeedValue.Value:0.00}x" : "OFF")}");
        GUI.Label(new Rect(x + 13f, y + 71f, 345f, 20f), $"{Plugin.EspKey.Value} ESP: {(Plugin.EspEnabled.Value ? $"ON ({_espTargets.Count})" : "OFF")}");
        GUI.Label(new Rect(x + 13f, y + 93f, 345f, 20f), $"{Plugin.AutoCleanupKey.Value} Auto Cleanup: {_cleanupPhase}");
        GUI.Label(new Rect(x + 13f, y + 115f, 345f, 20f),
            $"Cards: {_cleanupCardsRemaining}  Parts: {_fragmentPartsCount}/{_fragmentPartsNeeded} (+{_lastCleanupFragmentsCollected})  Trash: {_cleanupTrashRemaining}");
        GUI.Label(new Rect(x + 13f, y + 137f, 345f, 20f), $"Money: {(_lastMoneyValue >= 0 ? _lastMoneyValue.ToString() : "use menu to set")}");
    }
}
