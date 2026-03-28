using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace QuickRestart;

internal static class Log
{
    private static readonly string LogPath = System.IO.Path.Combine(
        OS.GetUserDataDir(), "logs", "QuickRestart.log");

    private static bool _cleared;

    public static void Write(string message)
    {
        try
        {
            if (!_cleared)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.WriteAllText(LogPath,
                    $"[{System.DateTime.Now:HH:mm:ss.fff}] === Log cleared (new session) ==={System.Environment.NewLine}");
                _cleared = true;
            }
            var line = $"[{System.DateTime.Now:HH:mm:ss.fff}] {message}{System.Environment.NewLine}";
            System.IO.File.AppendAllText(LogPath, line);
            GD.Print($"[QuickRestart] {message}");
        }
        catch { }
    }
}

[ModInitializer("Initialize")]
public static class QuickRestartMod
{
    private static bool _isRestarting;

    public static void Initialize()
    {
        var harmony = new Harmony("com.quickrestart.sts2");
        harmony.PatchAll(typeof(QuickRestartMod).Assembly);
    }

    /// <summary>
    /// Quick restart = quit to menu + continue, without showing the menu.
    /// The game auto-saves when entering each room, so loading that save
    /// and re-entering the room reproduces the exact same encounter.
    /// </summary>
    public static async Task DoQuickRestart()
    {
        if (_isRestarting)
            return;

        var runManager = RunManager.Instance;
        if (runManager is not { IsInProgress: true })
            return;

        _isRestarting = true;
        Log.Write("=== QuickRestart triggered ===");
        try
        {
            // 1. Load the auto-save (written when the current room was entered)
            var saveResult = SaveManager.Instance.LoadRunSave();
            if (!saveResult.Success || saveResult.SaveData == null)
            {
                Log.Write("No save found, aborting");
                return;
            }
            var save = saveResult.SaveData;
            Log.Write($"Save loaded: act={save.CurrentActIndex} preFinished={save.PreFinishedRoom?.RoomType}");

            // 2. Capture enemy positions before teardown (cosmetic: keeps enemies in same spots)
            var savedEnemyPositions = CaptureEnemyPositions();

            // 3. Tear down current run (mirrors NPauseMenu.CloseToMenu → NGame.ReturnToMainMenu)
            runManager.ActionQueueSet.Reset();
            NRunMusicController.Instance?.StopMusic();
            await NGame.Instance.Transition.FadeOut();
            runManager.CleanUp();
            Log.Write("Teardown complete");

            // 4. Reconstruct run state from save (mirrors NMainMenu.OnContinueButtonPressedAsync)
            var runState = RunState.FromSerializable(save);
            runManager.SetUpSavedSinglePlayer(runState, save);
            NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());
            Log.Write($"RunState reconstructed: act={runState.Act} floor={runState.TotalFloor}");

            // 5. Load the run (mirrors NGame.LoadRun exactly)
            //    Passing null lets the game re-enter the room naturally — same RNG seed,
            //    same encounter, same event. Map drawings are restored by LoadRun via
            //    RunManager.MapDrawingsToLoad (set by SetUpSavedSinglePlayer).
            await NGame.Instance.LoadRun(runState, null);
            Log.Write("LoadRun complete");

            // 6. Restore enemy positions (cosmetic — RandomizeEnemyScalesAndHues shifts them)
            RestoreEnemyPositions(savedEnemyPositions);

            // 7. Fade back in
            await NGame.Instance.Transition.FadeIn();
            Log.Write("=== QuickRestart complete ===");

            // 8. Restore map marker after scene settles
            //    LoadRun → GenerateMap → SetMap hides the marker; re-init it.
            if (runState.VisitedMapCoords.Count > 0)
            {
                var lastCoord = runState.VisitedMapCoords[runState.VisitedMapCoords.Count - 1];
                await NGame.Instance.ToSignal(NGame.Instance.GetTree(), SceneTree.SignalName.ProcessFrame);
                NMapScreen.Instance?.InitMarker(lastCoord);
            }
        }
        finally
        {
            _isRestarting = false;
        }
    }

    private static List<(uint combatId, Vector2 pos)> CaptureEnemyPositions()
    {
        var positions = new List<(uint combatId, Vector2 pos)>();
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return positions;

            // Capture alive enemies
            foreach (var node in combatRoom.CreatureNodes)
            {
                if (node.Entity.Side == CombatSide.Enemy)
                    positions.Add((node.Entity.CombatId ?? 0, node.Position));
            }
            // Capture dead/dying enemies (moved to RemovingCreatureNodes)
            foreach (var node in combatRoom.RemovingCreatureNodes)
            {
                if (GodotObject.IsInstanceValid(node) && node.Entity.Side == CombatSide.Enemy)
                    positions.Add((node.Entity.CombatId ?? 0, node.Position));
            }
            // Sort by CombatId to match post-restart ordering (all enemies alive, ordered by id)
            positions.Sort((a, b) => a.combatId.CompareTo(b.combatId));
            Log.Write($"Captured {positions.Count} enemy positions");
        }
        catch (Exception ex) { Log.Write($"Enemy position capture error: {ex.Message}"); }
        return positions;
    }

    private static void RestoreEnemyPositions(List<(uint combatId, Vector2 pos)> savedPositions)
    {
        if (savedPositions.Count == 0) return;
        try
        {
            var combatRoom = NCombatRoom.Instance;
            if (combatRoom == null) return;

            var enemyNodes = combatRoom.CreatureNodes
                .Where(n => n.Entity.Side == CombatSide.Enemy).ToList();

            if (enemyNodes.Count != savedPositions.Count)
            {
                Log.Write($"Position restore skipped: count mismatch ({enemyNodes.Count} vs {savedPositions.Count})");
                return;
            }
            for (int i = 0; i < enemyNodes.Count; i++)
            {
                enemyNodes[i].Position = savedPositions[i].pos;
            }
            Log.Write($"Restored {enemyNodes.Count} enemy positions");
        }
        catch (Exception ex) { Log.Write($"Enemy position restore error: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(NGame), "_Input")]
public static class PatchNGameInput
{
    [HarmonyPrefix]
    public static void Prefix(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true } key
            && key.Keycode == Key.F5
            && !key.Echo
            && RunManager.Instance?.IsInProgress == true
            && RunManager.Instance?.IsGameOver != true
            && !NGame.Instance.Transition.InTransition)
        {
            TaskHelper.RunSafely(QuickRestartMod.DoQuickRestart());
        }
    }
}
