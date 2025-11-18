using BepInEx;
using BepInEx.Logging;
using Fleck;
using HarmonyLib;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace ZeepkistDataStreamer
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
        private ConcurrentDictionary<Guid, IWebSocketConnection> allSockets = new ConcurrentDictionary<Guid, IWebSocketConnection>();
        public static SetupCar target; // Zeepkist car to track
        public static InputCommand latestCommand = new InputCommand();
        private MessagePackSerializerOptions options;

        public static ConcurrentQueue<InputCommand> commandQueue = new ConcurrentQueue<InputCommand>();
        public static ConcurrentQueue<bool> stateRequests = new ConcurrentQueue<bool>();


        private void Awake()
        {
            Instance = this;
            logger = Logger;

            harmony = new Harmony("andme123.zeepkistdatastreamer");
            harmony.PatchAll();

            logger.LogInfo("Plugin andme123.zeepkistdatastreamer is loaded!");

            StaticCompositeResolver.Instance.Register(
                UnityVectorResolver.Instance,
                StandardResolver.Instance
            );

            options = MessagePackSerializerOptions.Standard.WithResolver(StaticCompositeResolver.Instance);
            MessagePackSerializer.DefaultOptions = options;

        }

        private void Start()
        {
            // Start WebSocket server
            FleckLog.Level = Fleck.LogLevel.Warn;
            server = new WebSocketServer("ws://0.0.0.0:8080");
            server.Start(socket =>
            {
                socket.OnOpen = () => allSockets.TryAdd(socket.ConnectionInfo.Id, socket);
                socket.OnClose = () => allSockets.TryRemove(socket.ConnectionInfo.Id, out _);
                socket.OnBinary = bytes => HandleIncomingBinary(bytes);
                socket.OnMessage = message => logger.LogInfo($"[Streamer] Text message received: {message}");
                socket.OnError = ex => logger.LogError($"[Streamer] WebSocket error: {ex.Message}");
                
            });

            logger.LogInfo("[Streamer] WebSocket server started in ws://localhost:8080");

        }

        private void FixedUpdate()
        {
            if (Plugin.target == null)
                return;

            // handle queued inputs
            while (commandQueue.TryDequeue(out var cmd))
                latestCommand = cmd;

            // handle WS state requests
            while (stateRequests.TryDequeue(out _))
                SendData();

            //ApplyInput(latestCommand);
        }

        // Handle incoming binary messages (input commands)
        private void HandleIncomingBinary(byte[] packet)
        {
            try
            {
                var cmd = MessagePackSerializer.Deserialize<InputCommand>(packet);

                if (cmd.cmd == "ACTION")
                {
                    commandQueue.Enqueue(cmd);
                    return;
                }

                if (cmd.cmd == "STATE_REQUEST")
                {
                    stateRequests.Enqueue(true);
                }
            }
            catch (Exception e)
            {
                Plugin.logger.LogError($"Error parsing input: {e}");
            }
        }


        private void ApplyInput(InputCommand cmd)
        {
            if (target == null) return;

            // ---- STEER ----
            target.Inputs[0].SteerAction.axis = cmd.steer;

            // ---- BRAKE ----
            bool brakePressed = cmd.brake > 0.5f;
            target.Inputs[0].BrakeAction.buttonHeld = brakePressed;
            target.Inputs[0].BrakeAction.axis = cmd.brake;

            // ---- ARMS UP ----
            bool armsUpPressed = cmd.armsUp > 0.5f;
            target.Inputs[0].ArmsUpAction.buttonHeld = armsUpPressed;
            target.Inputs[0].ArmsUpAction.axis = cmd.armsUp;

            // ---- RESET ----
            // Reset (edge-triggered)
            if (cmd.reset > 0f && target.Inputs[0].ResetAction.buttonDown == false)
            {
                target.Inputs[0].ResetAction.buttonDown = true;
            }
            else
            {
                target.Inputs[0].ResetAction.buttonDown = false;
            }

            // logger.LogInfo($"[Streamer] Applied Input - Steer: {cmd.steer}, Brake: {cmd.brake}, ArmsUp: {cmd.armsUp}, Reset: {cmd.reset}");

        }



        // Set the target car to stream data from
        internal void SetTarget()
        {
            target = FindObjectOfType<SetupCar>();
            logger.LogInfo($"[Streamer] Target car set to {target.name}");
            logger.LogInfo($"[Streamer] playerNum: {target.cc.playerNum}");
            logger.LogInfo($"[Streamer] rb position: {target.cc.rb.position}");
            logger.LogInfo($"[Streamer] rb rotation: {target.cc.rb.rotation}");
            logger.LogInfo($"[Streamer] localVelocity: {target.cc.localVelocity}");
            logger.LogInfo($"[Streamer] localVelocity: {target.cc.localAngularVelocity}");
        }

        // Send data to all connected clients
        private void SendData()
        {
            // Check if target car is set and there are connected clients
            if (target == null || allSockets.Count == 0)
                return;

            // Gather data
            var pos = target.cc.rb.position;
            var rot = target.cc.rb.rotation.eulerAngles;
            var locVel = target.cc.localVelocity;
            var locAngVel = target.cc.localAngularVelocity;

            // Create data object
            StreamData data = new StreamData
            {
                state = new StateData
                {
                    position = pos,
                    rotation = rot,
                    localVelocity = locVel,
                    localAngularVelocity = locAngVel
                },
                timestamp = Time.time
            };


            // Encode data
            byte[] bytes = MessagePackSerializer.Serialize(data, options);
            foreach (var kvp in allSockets)
            {
                var id = kvp.Key;
                var socket = kvp.Value;

                if (socket.IsAvailable)
                {
                    socket.Send(bytes);
                }
                else
                {
                    Plugin.logger.LogWarning("[Streamer] Removing dead socket.");
                    allSockets.TryRemove(id, out _);
                    socket.Close();
                }
            }
        }



        private void OnDestroy()
        {
            // Clean up WebSocket connections
            foreach (var socket in allSockets)
                socket.Value.Close();
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

    // --- STATE DATA ---
    [MessagePackObject(keyAsPropertyName:true)]
    public class StateData
    {
        public Vector3 position;
        public Vector3 rotation;
        public Vector3 localVelocity;
        public Vector3 localAngularVelocity;

    }

    // --- STREAM DATA ---
    [MessagePackObject(keyAsPropertyName:true)]
    public class StreamData
    {
        public StateData state;
        public float timestamp;
    }

    // --- INPUT COMMAND ---
    [MessagePackObject(keyAsPropertyName:true)]
    public class InputCommand
    {
        public string cmd;
        public float steer;
        public float brake;
        public float armsUp;
        public float reset;
    }

    // --- MessagePack Formatters and Resolvers for Unity types ---
    // Vector3 Formatter
    public class Vector3Formatter : IMessagePackFormatter<Vector3>
    {
        public void Serialize(ref MessagePackWriter writer, Vector3 value, MessagePackSerializerOptions options)
        {
            writer.WriteArrayHeader(3);
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        public Vector3 Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            int count = reader.ReadArrayHeader();
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            return new Vector3(x, y, z);
        }
    }

    // Unity Resolver
    public class UnityVectorResolver : IFormatterResolver
    {
        public static readonly UnityVectorResolver Instance = new UnityVectorResolver();

        public IMessagePackFormatter<T> GetFormatter<T>()
        {
            if (typeof(T) == typeof(UnityEngine.Vector3))
                return (IMessagePackFormatter<T>)(object)new Vector3Formatter();

            return null;
        }
    }



}
