﻿using Harmony;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.Profile;

namespace ServerMod
{
    [StaticConstructorOnStartup]
    public class ServerMod
    {
        public const int DEFAULT_PORT = 30502;

        public static String username;
        public static Server server;
        public static Connection client;
        public static Connection localServerConnection;

        public static byte[] savedWorld;
        public static byte[] mapsData;
        public static bool saving = false;
        public static CountdownLock worldDownloading = new CountdownLock();
        public static AutoResetEvent pause = new AutoResetEvent(false);
        public static Dictionary<string, Faction> newFactions = new Dictionary<string, Faction>();
        public static XmlDocument clientFaction;

        public static Queue<ScheduledServerAction> actions = new Queue<ScheduledServerAction>();

        static ServerMod()
        {
            GenCommandLine.TryGetCommandLineArg("username", out username);
            if (username == null)
                username = SteamUtility.SteamPersonaName;
            if (username == "???")
                username = "Player" + Rand.Range(0, 9999);

            Log.Message("Player's username: " + username);

            var gameobject = new GameObject();
            gameobject.AddComponent<OnMainThread>();
            UnityEngine.Object.DontDestroyOnLoad(gameobject);

            var harmony = HarmonyInstance.Create("servermod");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    public static class Packets
    {
        public const int CLIENT_REQUEST_WORLD = 0;
        public const int CLIENT_WORLD_FINISHED = 1;
        public const int CLIENT_ACTION_REQUEST = 2;
        public const int CLIENT_USERNAME = 3;
        public const int CLIENT_NEW_WORLD_OBJ = 4;
        public const int CLIENT_MAP_DATA = 5;

        public const int SERVER_WORLD_DATA = 0;
        public const int SERVER_ACTION_SCHEDULE = 1;
        public const int SERVER_PAUSE_FOR_WORLD_DOWNLOAD = 2;
        public const int SERVER_UNPAUSE = 3;
        public const int SERVER_NEW_FACTIONS = 4;
        public const int SERVER_NEW_WORLD_OBJ = 5;
    }

    public enum ServerAction : int
    {
        PAUSE, UNPAUSE
    }

    public struct ScheduledServerAction
    {
        public readonly int ticks;
        public readonly ServerAction action;

        public ScheduledServerAction(int ticks, ServerAction action)
        {
            this.ticks = ticks;
            this.action = action;
        }
    }

    public static class Extensions
    {
        public static T[] Append<T>(this T[] arr1, T[] arr2)
        {
            T[] result = new T[arr1.Length + arr2.Length];
            Array.Copy(arr1, 0, result, 0, arr1.Length);
            Array.Copy(arr2, 0, result, arr1.Length, arr2.Length);
            return result;
        }

        public static T[] SubArray<T>(this T[] data, int index, int length)
        {
            T[] result = new T[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }

        public static void RemoveChildIfPresent(this XmlNode node, string child)
        {
            XmlNode childNode = node[child];
            if (childNode != null)
                node.RemoveChild(childNode);
        }
    }

    public class LocalClientConnection : Connection
    {
        public LocalServerConnection server;

        public LocalClientConnection() : base(null)
        {
        }

        public override void Send(int id, byte[] message = null)
        {
            message = message ?? new byte[] { 0 };
            server.State?.Message(id, message);
        }

        public override void Close()
        {
            connectionClosed();
            server.connectionClosed();
        }

        public override string ToString()
        {
            return "Local";
        }
    }

    public class LocalServerConnection : Connection
    {
        public LocalClientConnection client;

        public LocalServerConnection() : base(null)
        {
        }

        public override void Send(int id, byte[] message = null)
        {
            message = message ?? new byte[] { 0 };
            client.State?.Message(id, message);
        }

        public override void Close()
        {
            connectionClosed();
            client.connectionClosed();
        }

        public override string ToString()
        {
            return "Local";
        }
    }

    public class CountdownLock
    {
        public AutoResetEvent eventObj = new AutoResetEvent(false);
        private HashSet<object> ids = new HashSet<object>();

        public void Add(object id)
        {
            lock (ids)
            {
                ids.Add(id);
            }
        }

        public void Wait()
        {
            eventObj.WaitOne();
        }

        public bool Done(object id)
        {
            lock (ids)
            {
                if (!ids.Remove(id))
                    return false;

                if (ids.Count == 0)
                {
                    eventObj.Set();
                    return true;
                }

                return false;
            }
        }
    }

    public class ServerWorldState : ConnectionState
    {
        public ServerWorldState(Connection connection) : base(connection)
        {
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.CLIENT_REQUEST_WORLD)
            {
                OnMainThread.Queue(() =>
                {
                    if (ServerMod.savedWorld == null)
                    {
                        if (!ServerMod.saving)
                            SaveWorld();

                        LongEventHandler.QueueLongEvent(() => SendData(), "Sending the world (queued)", false, null);
                    }
                    else
                    {
                        SendData();
                    }
                });
            }
            else if (id == Packets.CLIENT_WORLD_FINISHED)
            {
                OnMainThread.Queue(() =>
                {
                    this.Connection.State = new ServerPlayingState(this.Connection);

                    if (ServerMod.worldDownloading.Done(Connection.connId))
                    {
                        ServerMod.savedWorld = null;

                        ScribeUtil.StartWriting();
                        Scribe.EnterNode("data");
                        ScribeUtil.Look(ref ServerMod.newFactions, "newFactions", LookMode.Value, LookMode.Deep);
                        byte[] factionData = ScribeUtil.FinishWriting();

                        ServerMod.server.SendToAll(Packets.SERVER_NEW_FACTIONS, factionData, ServerMod.localServerConnection);
                        ServerMod.newFactions.Clear();

                        ServerMod.server.SendToAll(Packets.SERVER_UNPAUSE);

                        Log.Message("world sending finished");
                    }
                });
            }
            else if (id == Packets.CLIENT_USERNAME)
            {
                OnMainThread.Queue(() => Connection.username = Encoding.ASCII.GetString(data));
            }
        }

        private void SendData()
        {
            ServerModWorldComp factions = Find.World.GetComponent<ServerModWorldComp>();
            Faction faction = null;
            factions.playerFactions.TryGetValue(Connection.username, out faction);
            if (faction == null)
            {
                faction = FactionGenerator.NewGeneratedFaction(FactionDefOf.PlayerColony);
                faction.Name = Connection.username + "'s faction";
                faction.def = FactionDefOf.Outlander;
                Find.FactionManager.Add(faction);
                factions.playerFactions[Connection.username] = faction;
                ServerMod.newFactions[Connection.username] = faction;

                ScribeUtil.StartWriting();
                Scribe.EnterNode("data");
                Scribe_Deep.Look(ref faction, "clientFaction");
                byte[] factionData = ScribeUtil.FinishWriting();
                Connection.Send(Packets.SERVER_NEW_FACTIONS, factionData);

                Log.Message("New faction: " + faction.Name);
            }

            string mapsFile = ServerPlayingState.GetPlayerMapsPath(Connection.username);
            byte[] mapsData = new byte[0];
            if (File.Exists(mapsFile))
            {
                using (MemoryStream stream = new MemoryStream())
                {
                    XmlDocument maps = new XmlDocument();
                    maps.Load(mapsFile);
                    maps.Save(stream);
                    mapsData = stream.ToArray();
                }
            }

            byte[] data = BitConverter.GetBytes(ServerMod.savedWorld.Length).Append(ServerMod.savedWorld).Append(BitConverter.GetBytes(mapsData.Length)).Append(mapsData);
            Connection.Send(Packets.SERVER_WORLD_DATA, data);

            ServerMod.worldDownloading.Add(Connection.connId);
        }

        private void SaveWorld()
        {
            ServerMod.server.SendToAll(Packets.SERVER_PAUSE_FOR_WORLD_DOWNLOAD, null, this.Connection, ServerMod.localServerConnection);
            ServerMod.saving = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;

            LongEventHandler.QueueLongEvent(() =>
            {
                ScribeUtil.StartWriting();

                Scribe.EnterNode("savegame");
                ScribeMetaHeaderUtility.WriteMetaHeader();
                Scribe.EnterNode("game");
                sbyte visibleMapIndex = -1;
                Scribe_Values.Look<sbyte>(ref visibleMapIndex, "visibleMapIndex", -1, false);
                typeof(Game).GetMethod("ExposeSmallComponents", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(Current.Game, null);
                World world = Current.Game.World;
                Scribe_Deep.Look<World>(ref world, "world");
                List<Map> maps = new List<Map>();
                Scribe_Collections.Look<Map>(ref maps, "maps", LookMode.Deep);
                Scribe.ExitNode();

                ServerMod.savedWorld = ScribeUtil.FinishWriting();
                ServerMod.saving = false;

                LongEventHandler.QueueLongEvent(() =>
                {
                    ServerMod.worldDownloading.Wait();
                }, "Sending the world", true, null);
            }, "Saving world for incoming players", false, null);
        }

        public override void Disconnect()
        {
        }
    }

    public class ServerPlayingState : ConnectionState
    {
        public ServerPlayingState(Connection connection) : base(connection)
        {
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.CLIENT_ACTION_REQUEST)
            {
                OnMainThread.Queue(() =>
                {
                    ServerAction action = (ServerAction)BitConverter.ToInt32(data, 0);
                    ScheduledServerAction schdl = new ScheduledServerAction(Find.TickManager.TicksGame + 15, action);

                    byte[] send = BitConverter.GetBytes((int)schdl.ticks).Append(BitConverter.GetBytes((int)action));
                    ServerMod.server.SendToAll(Packets.SERVER_ACTION_SCHEDULE, send);

                    Log.Message("server got request from client at " + Find.TickManager.TicksGame + " for " + action + " " + schdl.ticks);
                });
            }
            else if (id == Packets.CLIENT_NEW_WORLD_OBJ)
            {
                ServerMod.server.SendToAll(Packets.SERVER_NEW_WORLD_OBJ, data, this.Connection);
            }
            else if (id == Packets.CLIENT_MAP_DATA)
            {
                OnMainThread.Queue(() =>
                {
                    try
                    {
                        using (MemoryStream stream = new MemoryStream(data))
                        using (XmlTextReader xml = new XmlTextReader(stream))
                        {
                            XmlDocument xmlDocument = new XmlDocument();
                            xmlDocument.Load(xml);
                            xmlDocument.Save(GetPlayerMapsPath(Connection.username));
                        }
                    }
                    catch (XmlException e)
                    {
                        Log.Error("Couldn't save " + Connection.username + "'s maps");
                        Log.Error(e.ToString());
                    }
                });
            }
        }

        public override void Disconnect()
        {
        }

        public static string GetPlayerMapsPath(string username)
        {
            string worldfolder = Path.Combine(Path.Combine(GenFilePaths.SaveDataFolderPath, "MpSaves"), Find.World.GetComponent<ServerModWorldComp>().worldId);
            DirectoryInfo directoryInfo = new DirectoryInfo(worldfolder);
            if (!directoryInfo.Exists)
                directoryInfo.Create();
            return Path.Combine(worldfolder, username + ".maps");
        }
    }

    public class ClientWorldState : ConnectionState
    {
        public ClientWorldState(Connection connection) : base(connection)
        {
            connection.Send(Packets.CLIENT_USERNAME, Encoding.ASCII.GetBytes(ServerMod.username));
            connection.Send(Packets.CLIENT_REQUEST_WORLD);
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.SERVER_WORLD_DATA)
            {
                OnMainThread.Queue(() =>
                {
                    int worldLen = BitConverter.ToInt32(data, 0);
                    ServerMod.savedWorld = data.SubArray(4, worldLen);
                    int mapsLen = BitConverter.ToInt32(data, worldLen + 4);
                    ServerMod.mapsData = data.SubArray(worldLen + 8, mapsLen);

                    Log.Message("World size: " + worldLen + ", Maps size: " + mapsLen);

                    LongEventHandler.QueueLongEvent(() =>
                    {
                        MemoryUtility.ClearAllMapsAndWorld();
                        Current.Game = new Game();
                        Current.Game.InitData = new GameInitData();
                        Current.Game.InitData.gameToLoad = "server";
                    }, "Play", "LoadingLongEvent", true, null);
                });
            }
            else if (id == Packets.SERVER_NEW_FACTIONS)
            {
                OnMainThread.Queue(() =>
                {
                    using (MemoryStream stream = new MemoryStream(data))
                    using (XmlTextReader xml = new XmlTextReader(stream))
                    {
                        XmlDocument xmlDocument = new XmlDocument();
                        xmlDocument.Load(xml);
                        ServerMod.clientFaction = xmlDocument;
                    }
                });
            }
        }

        public override void Disconnect()
        {
        }
    }

    public class ClientPlayingState : ConnectionState
    {
        public ClientPlayingState(Connection connection) : base(connection)
        {
        }

        public override void Message(int id, byte[] data)
        {
            if (id == Packets.SERVER_ACTION_SCHEDULE)
            {
                OnMainThread.Queue(() =>
                {
                    ScheduledServerAction schdl = new ScheduledServerAction(BitConverter.ToInt32(data, 0), (ServerAction)BitConverter.ToInt32(data, 4));
                    ServerMod.actions.Enqueue(schdl);
                    Log.Message("client got request from server at " + Find.TickManager.TicksGame + " for action " + schdl.action + " " + schdl.ticks);
                });
            }
            else if (id == Packets.SERVER_PAUSE_FOR_WORLD_DOWNLOAD)
            {
                LongEventHandler.QueueLongEvent(() =>
                {
                    ServerMod.pause.WaitOne();
                }, "Waiting for other players to load", true, null);
            }
            else if (id == Packets.SERVER_UNPAUSE)
            {
                ServerMod.pause.Set();
            }
            else if (id == Packets.SERVER_NEW_FACTIONS)
            {
                OnMainThread.Queue(() =>
                {
                    ScribeUtil.StartLoading(data);
                    ScribeUtil.SupplyCrossRefs();
                    ScribeUtil.Look(ref ServerMod.newFactions, "newFactions", LookMode.Value, LookMode.Deep);
                    ScribeUtil.FinishLoading();

                    foreach (KeyValuePair<string, Faction> pair in ServerMod.newFactions)
                    {
                        // our own faction has been already sent
                        if (pair.Key == Connection.username) continue;

                        Find.FactionManager.Add(pair.Value);
                        Find.World.GetComponent<ServerModWorldComp>().playerFactions[pair.Key] = pair.Value;
                    }

                    Log.Message("Got " + ServerMod.newFactions.Count + " new factions");

                    ServerMod.newFactions.Clear();
                });
            }
            else if (id == Packets.SERVER_NEW_WORLD_OBJ)
            {
                OnMainThread.Queue(() =>
                {
                    ScribeUtil.StartLoading(data);
                    ScribeUtil.SupplyCrossRefs();
                    WorldObject obj = null;
                    Scribe_Deep.Look(ref obj, "worldObj");
                    ScribeUtil.FinishLoading();
                    Find.WorldObjects.Add(obj);
                });
            }
        }

        public override void Disconnect()
        {
        }

        // Currently covers:
        // - settling after joining
        public static void SyncWorldObj(WorldObject obj)
        {
            ScribeUtil.StartWriting();
            Scribe.EnterNode("data");
            Scribe_Deep.Look(ref obj, "worldObj");
            byte[] data = ScribeUtil.FinishWriting();
            ServerMod.client.Send(Packets.CLIENT_NEW_WORLD_OBJ, data);
        }
    }

    public class OnMainThread : MonoBehaviour
    {
        private static readonly Queue<Action> queue = new Queue<Action>();

        public void Update()
        {
            lock (queue)
                while (queue.Count > 0)
                    queue.Dequeue().Invoke();

            if (Current.Game != null)
                // when paused, execute immediately
                while (ServerMod.actions.Count > 0 && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused)
                    ExecuteServerAction(ServerMod.actions.Dequeue());
        }

        public static void Queue(Action action)
        {
            lock (queue)
                queue.Enqueue(action);
        }

        public static void ExecuteServerAction(ScheduledServerAction action)
        {
            if (action.action == ServerAction.PAUSE)
                TickUpdatePatch.SetSpeed(TimeSpeed.Paused);
            else if (action.action == ServerAction.UNPAUSE)
                TickUpdatePatch.SetSpeed(TimeSpeed.Normal);

            Log.Message("executed a scheduled action " + action.action);
        }
    }

    public class ServerModWorldComp : WorldComponent
    {
        public Dictionary<string, Faction> playerFactions = new Dictionary<string, Faction>();
        public string worldId = Guid.NewGuid().ToString();

        private List<string> keyWorkingList;
        private List<Faction> valueWorkingList;

        public ServerModWorldComp(World world) : base(world)
        {
        }

        public override void ExposeData()
        {
            ScribeUtil.Look(ref playerFactions, "playerFactions", LookMode.Value, LookMode.Reference, ref keyWorkingList, ref valueWorkingList);
            Scribe_Values.Look(ref worldId, "worldId", null);
        }

    }

}

