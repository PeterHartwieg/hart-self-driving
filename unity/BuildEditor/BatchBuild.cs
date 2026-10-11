#nullable enable
using System;
using System.Linq;
using BAModTemplate.Editor;
using UnityEditor;
using UnityEngine;

namespace DrivenRides.BuildEditor
{
    // Entry point for `Unity -batchmode -executeMethod DrivenRides.BuildEditor.BatchBuild.Build -modId <id>`.
    // Runs the SDK's own validator and packager, then exits with 0 on success and 1 on failure.
    public static class BatchBuild
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);
        private static bool finished;

        public static void Build()
        {
            var deadline = DateTime.UtcNow + Timeout;
            EditorApplication.update += () =>
            {
                if (!finished && DateTime.UtcNow > deadline) Finish(1, $"No result from the packager after {Timeout.TotalMinutes} minutes.");
            };
            var modId = ArgValue("-modId") ?? "DrivenRides";
            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                var all = ModDiscovery.DiscoverAll();
                var mod = all.FirstOrDefault(m => m.Manifest.ModId == modId);
                if (mod == null) { Finish(1, $"No mod with ModId '{modId}' under Assets/Mods."); return; }

                var issues = ModValidator.Validate(mod, all);
                foreach (var issue in issues) Debug.Log($"[DrivenRidesBuild] {issue.Severity}: {issue.Message}");
                if (issues.Any(i => i.Severity == Severity.Error)) { Finish(1, "Validation failed."); return; }

                ModPackager.JobChanged += job =>
                {
                    if (job.Mod.Manifest.ModId != modId || !job.IsTerminal) return;
                    foreach (var line in job.Log) Debug.Log("[DrivenRidesBuild] " + line);
                    foreach (var msg in job.CompilerMessages) Debug.Log($"[DrivenRidesBuild] {msg.type}: {msg.message}");
                    Finish(job.State == BuildState.Done ? 0 : 1, job.StatusText + " Output: " + job.OutputDirectoryAbsolute);
                };
                ModPackager.Enqueue(mod, installAfterBuild: false, revealWhenDone: false);
            }
            catch (Exception ex)
            {
                Finish(1, ex.ToString());
            }
        }

        private static void Finish(int code, string message)
        {
            if (finished) return;
            finished = true;
            Debug.Log($"[DrivenRidesBuild] {(code == 0 ? "OK" : "FAILED")}: {message}");
            EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }

        private static string? ArgValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
