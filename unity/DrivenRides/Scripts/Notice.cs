#nullable enable
using System;
using System.Collections.Generic;
using UI.Notification;

namespace DrivenRides
{
    // Player-facing notices. Text shows a line from Locales/en.json; Show shows a raw English message and is for
    // test-mode texts only (debug keys, the rig, detailed reasons), which a normal game never meets.
    internal static class Notice
    {
        public static void Text(string key, params (string Key, string Value)[] values)
        {
            try
            {
                var data = new Dictionary<string, string>();
                foreach (var (k, v) in values) data[k] = v;
                Notifications.Show(NotificationType.Info, key, data, 5f, trackOnSaveGame: false);
            }
            catch (Exception ex) { Log.Error("Notice: " + ex.Message); }
        }

        public static void Show(string message)
        {
            try
            {
                Notifications.Show(NotificationType.Info, "drivenrides:notification",
                    new Dictionary<string, string> { { "message", message } }, 5f, trackOnSaveGame: false);
            }
            catch (Exception ex) { Log.Error("Notice: " + ex.Message); }
        }
    }
}
