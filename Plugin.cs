using BepInEx;
using BepInEx.Logging;
using Fleck;
using HarmonyLib;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using System;
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
        private List<IWebSocketConnection> socketsToClose = new List<IWebSocketConnection>();
        public static New_ControlCar target; // Zeepkist car to track
        private InputCommand latestCommand = new InputCommand();
        private bool hasCommand = false;
        private MessagePackSerializerOptions options;


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
                socket.OnOpen = () => allSockets.Add(socket);
                socket.OnClose = () => allSockets.Remove(socket);
                socket.OnBinary = bytes => HandleIncomingBinary(bytes);
                socket.OnMessage = message => logger.LogInfo($"[Streamer] Text message received: {message}");
                socket.OnError = ex => logger.LogError($"[Streamer] WebSocket error: {ex.Message}");
                
            });

            logger.LogInfo("[Streamer] WebSocket server started in ws://localhost:8080");

        }

        private void FixedUpdate()
        {
            if (!hasCommand || Plugin.target == null)
                return;

            if (allSockets.Count == 0)
            {
                latestCommand = new InputCommand(); // Reset to default
                hasCommand = false;
                return; // No clients connected
            }

            ApplyInput(latestCommand);   // <-- always run once per frame
        }

        private void HandleIncomingBinary(byte[] packet)
        {
            try
            {
                var cmd = MessagePackSerializer.Deserialize<InputCommand>(packet);

                if (cmd == null)
                {
                    logger.LogError("[Streamer] Received null command.");
                    return;
                }

                if (cmd.cmd == "STATE_REQUEST")
                {
                    //logger.LogInfo("[Streamer] STATE_REQUEST received.");
                    SendData();
                }

                else if (cmd.cmd == "ACTION")
                {
                    //logger.LogInfo("[Streamer] ACTION command received.");
                    latestCommand = cmd;   // <-- store
                    hasCommand = true;
                    //logger.LogInfo($"[Streamer] Received Input - Steer: {cmd.steer}, Brake: {cmd.brake}, ArmsUp: {cmd.armsUp}, Reset: {cmd.reset}");
                }
                else
                {
                    logger.LogWarning($"[Streamer] Unknown command received: {cmd.cmd}");
                }
            }
            catch (Exception e)
            {
                Plugin.logger.LogError($"Error parsing input: {e}");
            }
        }

        private void ApplyInput(InputCommand cmd)
        {
            // Looking for a better approach right now
            // This is kinda buggy 

            if (target == null) return;

            // ---- STEER ----
            target.SteerAction2.axis = cmd.steer;

            // ---- BRAKE ----
            bool brakePressed = cmd.brake > 0.5f;
            target.BrakeAction2.buttonHeld = brakePressed;
            target.BrakeAction2.axis = cmd.brake;

            // ---- ARMS UP ----
            bool armsUpPressed = cmd.armsUp > 0.5f;
            target.ArmsUpAction2.buttonHeld = armsUpPressed;
            target.ArmsUpAction2.axis = cmd.armsUp;

            // ---- RESET ----
            // Reset (edge-triggered)
            if (cmd.reset > 0f && target.ResetAction.buttonDown == false)
            {
                target.ResetAction.buttonDown = true;
            }
            else
            {
                target.ResetAction.buttonDown = false;
            }

            //logger.LogInfo($"[Streamer] Applied Input - Steer: {cmd.steer}, Brake: {cmd.brake}, ArmsUp: {cmd.armsUp}, Reset: {cmd.reset}");

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

        // Send data to all connected clients
        private void SendData()
        {
            // Check if target car is set and there are connected clients
            if (target == null || allSockets.Count == 0)
                return;

            // Clear list of sockets to close
            socketsToClose.Clear();

            // Gather data
            var pos = target.rb.position;
            var rot = target.rb.rotation.eulerAngles;
            var locVel = target.localVelocity;
            var locAngVel = target.localAngularVelocity;

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

            // Send to all sockets
            foreach (var socket in allSockets)
            {
                if (socket.IsAvailable)
                {
                    socket.Send(bytes);
                    //logger.LogInfo("[Streamer] Sent data to client.");
                }
                else
                {
                    socket.Close();
                    socketsToClose.Add(socket);
                    Plugin.logger.LogWarning("[Streamer] Socket not available when sending data.");
                }
            }

            // Clean up closed sockets
            foreach (var socket in socketsToClose)
            {
                allSockets.Remove(socket);
            }
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

    // --- STATE DATA ---
    [MessagePackObject(keyAsPropertyName:true)]
    public class StateData
    {
        [Key(0)] public Vector3 position;
        [Key(1)] public Vector3 rotation;
        [Key(2)] public Vector3 localVelocity;
        [Key(3)] public Vector3 localAngularVelocity;

    }

    // --- STREAM DATA ---
    [MessagePackObject(keyAsPropertyName:true)]
    public class StreamData
    {
        [Key(0)] public StateData state;
        [Key(1)] public float timestamp;
    }

    // --- INPUT COMMAND ---
    [MessagePackObject(keyAsPropertyName:true)]
    public class InputCommand
    {
        [Key(0)] public string cmd;
        [Key(1)] public float steer;
        [Key(2)] public float brake;
        [Key(3)] public float armsUp;
        [Key(4)] public float reset;
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
