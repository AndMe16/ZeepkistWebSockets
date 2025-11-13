using BepInEx;
using BepInEx.Logging;
using Fleck;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ZeepkistDataStreamer;

namespace ZeepkistWebSockets
{
    [BepInPlugin("andme123.zeepkistdatastreamer", "ZeepkistDataStreamer", MyPluginInfo.PLUGIN_VERSION)]

    // Main plugin class
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource logger;
        private Harmony harmony;

        public static Plugin Instance { get; private set; }

        // WebSocket server and connections
        private WebSocketServer server;
        private List<IWebSocketConnection> allSockets = new List<IWebSocketConnection>();
        public static New_ControlCar target; // Zeepkist car to track


        private void Awake()
        {
            Instance = this;
            logger = Logger;

            harmony = new Harmony("andme123.zeepkistdatastreamer");
            harmony.PatchAll();

            logger.LogInfo("Plugin andme123.zeepkistdatastreamer is loaded!");
        }

        private void Start()
        {
            // Start WebSocket server
            FleckLog.Level = Fleck.LogLevel.Warn;
            server = new WebSocketServer("ws://0.0.0.0:8080");
            server.Start(socket =>
            {
                socket.OnOpen = () => allSockets.Add(socket);
                socket.OnClose = () => allSockets.Remove(socket);
            });

            logger.LogInfo("[Streamer] WebSocket server started in ws://localhost:8080");

            // Start data sending loop
            StartCoroutine(SendDataLoop());
        }


        // Set the target car to stream data from
        internal void SetTarget()
        {
            target = FindObjectOfType<New_ControlCar>();
            logger.LogInfo($"[Streamer] Target car set to {target.name}");
            logger.LogInfo($"[Streamer] playerNum: {target.playerNum}");
            logger.LogInfo($"[Streamer] rb position: {target.rb.position}");
            logger.LogInfo($"[Streamer] rb rotation: {target.rb.rotation}");
            logger.LogInfo($"[Streamer] localVelocity: {target.localVelocity}");
            logger.LogInfo($"[Streamer] localVelocity: {target.localAngularVelocity}");
        }

        // Coroutine to send data at regular intervals
        IEnumerator SendDataLoop()
        {
            float interval = 1f; // 1 second interval

            while (true)
            {
                SendData();
                yield return new WaitForSeconds(interval);
            }
        }

        // Send data to all connected clients
        private void SendData()
        {
            // Check if target car is set and there are connected clients
            if (target == null || allSockets.Count == 0)
                return;

            // Gather data
            var pos = target.rb.position;
            var rot = target.rb.rotation.eulerAngles;
            var locVel = target.localVelocity;
            var locAngVel = target.localAngularVelocity;

            // Create data object
            StreamData data = new StreamData()
            {
                position = pos,
                rotation = rot,
                localVelocity = locVel,
                localAngularVelocity = locAngVel
            };

            // Serialize
            string json = JsonUtility.ToJson(data);

            // Send to all sockets
            foreach (var socket in allSockets)
                socket.Send(json);
        }



        private void OnDestroy()
        {
            // Clean up WebSocket connections
            foreach (var socket in allSockets)
                socket.Close();
            allSockets.Clear();
            server?.Dispose();

            harmony?.UnpatchSelf();
            harmony = null;
        }
    }

    // Patches
    // GameMaster_SpawnPlayers patch to set target car after players are spawned
    [HarmonyPatch(typeof(GameMaster), "SpawnPlayers")]
    internal class PatchStreamer_GameMaster_SpawnPlayers
    {
        private static void Postfix(GameMaster __instance)
        {
            if (__instance is null) throw new ArgumentNullException(nameof(__instance));

            Plugin.logger.LogInfo("SpawnPlayers called in GameMaster.");

            // Only set target in single player mode
            if (!__instance.manager.singlePlayer) return;

            // Set the target car for streaming
            Plugin.Instance.SetTarget();
        }
    }

    // Data structure for streaming
    [Serializable]
    public class StreamData
    {
        public Vector3 position;
        public Vector3 rotation;
        public Vector3 localVelocity;
        public Vector3 localAngularVelocity;
    }
}
