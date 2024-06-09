﻿using ft.Bandwidth;
using ft.Commands;
using ft.IO;
using ft.Listeners;
using ft.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class SharedFileManager(string readFromFilename, string writeToFilename, int purgeSizeInBytes, int tunnelTimeoutMilliseconds) : StreamEstablisher
    {
        readonly Dictionary<int, BlockingCollection<byte[]>> ReceiveQueue = [];
        readonly BlockingCollection<Command> SendQueue = new(1);    //using a queue size of one makes the TCP receiver synchronous

        const int reportIntervalMs = 1000;
        readonly BandwidthTracker sentBandwidth = new(100, reportIntervalMs);
        readonly BandwidthTracker receivedBandwidth = new(100, reportIntervalMs);
        readonly BlockingCollection<Ping> pingResponsesReceived = [];
        public void ReportNetworkPerformance()
        {
            var pingRequest = new Ping(EnumPingType.Request);
            var pingStopwatch = new Stopwatch();

            while (true)
            {
                try
                {
                    var sentBandwidthStr = sentBandwidth.GetBandwidth();
                    var receivedBandwidthStr = receivedBandwidth.GetBandwidth();

                    pingStopwatch.Restart();
                    SendQueue.Add(pingRequest);

                    string? pingDurationStr = null;

                    var responseTimeout = new CancellationTokenSource(tunnelTimeoutMilliseconds);
                    try
                    {
                        while (true)
                        {
                            var pingResponse = pingResponsesReceived.GetConsumingEnumerable(responseTimeout.Token).First();
                            if (pingRequest.PacketNumber == pingResponse.ResponseToPacketNumber)
                            {
                                pingStopwatch.Stop();
                                pingDurationStr = $"RTT: {pingStopwatch.ElapsedMilliseconds:N0} ms";
                                break;
                            }
                        }
                    }
                    catch { }



                    Console.Write($"{DateTime.Now}: Counterpart: ");

                    if (IsOnline)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.Write($"{"Online",-10}");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write($"{"Offline",-10}");
                    }


                    var logStr = $"Rx: {receivedBandwidthStr,-12} Tx: {sentBandwidthStr,-12}";
                    if (pingDurationStr != null)
                    {
                        logStr += $" {pingDurationStr}";
                    }

                    Console.ForegroundColor = Program.OriginalConsoleColour;
                    Console.WriteLine(logStr);

                    Thread.Sleep(reportIntervalMs);
                }
                catch (Exception ex)
                {
                    Program.Log($"{ex}");
                }
            }
        }

        public byte[]? Read(int connectionId)
        {
            if (!ReceiveQueue.TryGetValue(connectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
            {
                return null;
            }

            byte[]? result = null;
            try
            {
                result = connectionReceiveQueue.Take(cancellationTokenSource.Token);
            }
            catch (InvalidOperationException)
            {
                //This is normal - the queue might have been marked as AddingComplete while we were listening
            }

            return result;
        }

        public void Connect(int connectionId)
        {
            var connectCommand = new Connect(connectionId);
            SendQueue.Add(connectCommand);

            if (!ReceiveQueue.TryGetValue(connectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
            {
                connectionReceiveQueue = [];
                ReceiveQueue.Add(connectionId, connectionReceiveQueue);
            }
        }

        public void Write(int connectionId, byte[] data)
        {
            var forwardCommand = new Forward(connectionId, data);
            SendQueue.Add(forwardCommand);
        }

        public void TearDown(int connectionId)
        {
            var teardownCommand = new TearDown(connectionId);
            SendQueue.Add(teardownCommand);

            ReceiveQueue.Remove(connectionId);
        }

        const long SESSION_ID = 0;
        const int READY_FOR_PURGE_FLAG = sizeof(long);
        const int PURGE_COMPLETE_FLAG = READY_FOR_PURGE_FLAG + 1;
        const int MESSAGE_WRITE_POS = PURGE_COMPLETE_FLAG + 1;

        ToggleWriter? setReadyForPurge;
        ToggleWriter? setPurgeComplete;

        public void SendPump()
        {
            var writeFileShortName = Path.GetFileName(WriteToFilename);

            try
            {
                //the writer always creates the file
                var fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, PurgeSizeInBytes * 2); //large buffer to prevent FileStream from autoflushing
                fileStream.SetLength(MESSAGE_WRITE_POS);

                var binaryWriter = new BinaryWriter(fileStream);
                var binaryReader = new BinaryReader(fileStream, Encoding.ASCII);

                var sessionId = Program.Random.NextInt64();
                binaryWriter.Write(sessionId);
                //Program.Log($"[{writeFileShortName}] Set Session ID to: {sessionId}");

                var setReadyForPurgeStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                setReadyForPurge = new ToggleWriter(
                    new BinaryWriter(setReadyForPurgeStream),
                    READY_FOR_PURGE_FLAG);

                var setPurgeCompleteStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                setPurgeComplete = new ToggleWriter(
                    new BinaryWriter(setPurgeCompleteStream),
                    PURGE_COMPLETE_FLAG);

                var ms = new MemoryStream();
                var msWriter = new BinaryWriter(ms);

                fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);

                foreach (var command in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                {
                    ms.SetLength(0);
                    command.Serialise(msWriter);

                    if (fileStream.Position + ms.Length >= PurgeSizeInBytes - MESSAGE_WRITE_POS)
                    {
                        Program.Log($"[{writeFileShortName}] Instructing counterpart to prepare for purge.");

                        var purge = new Purge();
                        purge.Serialise(binaryWriter);

                        //wait for counterpart to be ready for purge
                        isReadyForPurge?.Wait(1);

                        //perform the purge
                        fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                        fileStream.SetLength(MESSAGE_WRITE_POS);

                        //signal that the purge is complete
                        setPurgeComplete.Set(1);

                        //wait for counterpart clear their ready flag
                        isReadyForPurge?.Wait(0);

                        //clear our complete flag
                        setPurgeComplete.Set(0);

                        Program.Log($"[{writeFileShortName}] Purge complete.");
                    }

                    //write the message to file
                    var commandStartPos = fileStream.Position;
                    command.Serialise(binaryWriter);
                    var commandEndPos = fileStream.Position;

                    //Program.Log($"[{writeFileShortName}] Wrote packet number {command.PacketNumber:N0} ({command.GetType().Name}) to position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");

                    if (command is Forward forward && forward.Payload != null)
                    {
                        var totalBytesSent = sentBandwidth.TotalBytesTransferred + (ulong)forward.Payload.Length;
                        sentBandwidth.SetTotalBytesTransferred(totalBytesSent);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[{writeFileShortName}] {nameof(SendPump)}: {ex.Message}");
                Environment.Exit(1);
            }
        }

        ToggleReader? isReadyForPurge;
        ToggleReader? isPurgeComplete;

        public static long ReadSessionId(BinaryReader binaryReader)
        {
            var originalPos = binaryReader.BaseStream.Position;
            binaryReader.BaseStream.Seek(SESSION_ID, SeekOrigin.Begin);
            var result = binaryReader.ReadInt64();

            binaryReader.BaseStream.Seek(originalPos, SeekOrigin.Begin);

            return result;
        }

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            var readFileShortName = Path.GetFileName(ReadFromFilename);
            var checkForSessionChange = new Stopwatch();

            while (true)
            {
                try
                {
                    FileStream? fileStream = null;
                    BinaryReader? binaryReader = null;
                    long currentSessionId;

                    try
                    {
                        var fileAlreadyExisted = File.Exists(ReadFromFilename) && new FileInfo(ReadFromFilename).Length > 0;
                        if (!fileAlreadyExisted)
                        {
                            while (true)
                            {
                                if (File.Exists(ReadFromFilename) && new FileInfo(ReadFromFilename).Length > 0)
                                {
                                    Program.Log($"[{readFileShortName}] now exists. Reading.");
                                    break;
                                }
                                Thread.Sleep(200);
                            }
                        }

                        fileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                        if (fileAlreadyExisted)
                        {
                            Program.Log($"[{readFileShortName}] already existed. Seeking to end ({fileStream.Length:N0})");
                            fileStream.Seek(0, SeekOrigin.End);
                        }
                        else
                        {
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                        }

                        binaryReader = new BinaryReader(fileStream, Encoding.ASCII);

                        currentSessionId = ReadSessionId(binaryReader);
                        //Program.Log($"[{readFileShortName}] Read Session ID: {currentSessionId}");


                        var isReadyForPurgeStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isReadyForPurge = new ToggleReader(
                            new BinaryReader(isReadyForPurgeStream, Encoding.ASCII),
                            READY_FOR_PURGE_FLAG);

                        var isPurgeCompleteStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isPurgeComplete = new ToggleReader(
                            new BinaryReader(isPurgeCompleteStream, Encoding.ASCII),
                            PURGE_COMPLETE_FLAG);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[{readFileShortName}] Establish file: {ex}");
                        Environment.Exit(1);
                        return;
                    }

                    checkForSessionChange.Restart();
                    while (true)
                    {
                        while (true)
                        {
                            var nextByte = binaryReader.PeekChar();
                            if (nextByte != -1 && nextByte != 0)
                            {
                                break;
                            }

                            fileStream.Flush(); //force read

                            if (checkForSessionChange.ElapsedMilliseconds > 1000)
                            {
                                var latestSessionId = ReadSessionId(binaryReader);

                                if (latestSessionId != currentSessionId)
                                {
                                    throw new Exception($"New session detected.");
                                }

                                checkForSessionChange.Restart();
                            }

                            //Program.Log($"[{readFileShortName}] waiting for data at position {fileStream.Position:N0}.")

                            Delay.Wait(1);
                        }

                        var commandStartPos = fileStream.Position;
                        var command = Command.Deserialise(binaryReader);
                        var commandEndPos = fileStream.Position;

                        if (command == null)
                        {
                            Program.Log($"[{readFileShortName}] Could not read command at file position {commandStartPos:N0}. [{ReadFromFilename}]", ConsoleColor.Red);
                            Environment.Exit(1);
                        }

                        lastContactWithCounterpart = DateTime.Now;

                        //Program.Log($"[{readFileShortName}] Received packet number {command.PacketNumber:N0} ({command.GetType().Name}) from position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");

                        if (command is Forward forward && forward.Payload != null)
                        {
                            if (ReceiveQueue.TryGetValue(forward.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                            {
                                connectionReceiveQueue.Add(forward.Payload);

                                var totalBytesReceived = receivedBandwidth.TotalBytesTransferred + (ulong)(forward.Payload.Length);
                                receivedBandwidth.SetTotalBytesTransferred(totalBytesReceived);
                            }
                        }
                        else if (command is Connect connect)
                        {
                            if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                            {
                                var connectionReceiveQueue = new BlockingCollection<byte[]>();
                                ReceiveQueue.Add(connect.ConnectionId, connectionReceiveQueue);

                                var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                                StreamEstablished?.Invoke(this, sharedFileStream);
                            }
                        }
                        else if (command is Purge)
                        {
                            Program.Log($"[{readFileShortName}] Counterpart is about to purge this file.");

                            //signal that we're ready for purge
                            setReadyForPurge?.Set(1);

                            //wait for the purge to be complete
                            isPurgeComplete.Wait(1);

                            //go back to the beginning
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                            fileStream.Flush(); //force read

                            //clear our ready flag
                            setReadyForPurge?.Set(0);

                            //wait for counterpart to clear the complete flag
                            isPurgeComplete.Wait(0);

                            Program.Log($"[{readFileShortName}] File was purged by counterpart.");
                        }
                        else if (command is TearDown teardown && ReceiveQueue.TryGetValue(teardown.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                        {
                            Program.Log($"[{readFileShortName}] Counterpart asked to tear down connection {teardown.ConnectionId}");

                            ReceiveQueue.Remove(teardown.ConnectionId);

                            connectionReceiveQueue.CompleteAdding();
                        }
                        else if (command is Ping ping)
                        {
                            if (ping.PingType == EnumPingType.Request)
                            {
                                var response = new Ping(EnumPingType.Response)
                                {
                                    ResponseToPacketNumber = ping.PacketNumber
                                };
                                SendQueue.Add(response);
                            }

                            if (ping.PingType == EnumPingType.Response)
                            {
                                pingResponsesReceived.Add(ping);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[{readFileShortName}] {nameof(ReceivePump)}: {ex.Message}");
                    Program.Log($"[{readFileShortName}] Restarting {nameof(ReceivePump)}");
                }
            }
        }

        public override void Start()
        {
            Threads.StartNew(ReceivePump, nameof(ReceivePump));
            Threads.StartNew(SendPump, nameof(SendPump));
            Threads.StartNew(ReportNetworkPerformance, nameof(ReportNetworkPerformance));
            Threads.StartNew(MonitorOnlineStatus, nameof(MonitorOnlineStatus));
        }

        DateTime? lastContactWithCounterpart = null;
        public bool IsOnline { get; protected set; } = false;
        public event EventHandler<OnlineStatusEventArgs>? OnlineStatusChanged;

        private void MonitorOnlineStatus()
        {
            try
            {
                while (true)
                {
                    if (lastContactWithCounterpart != null)
                    {
                        var orig = IsOnline;

                        var timeSinceLastContact = DateTime.Now - lastContactWithCounterpart.Value;
                        IsOnline = timeSinceLastContact.TotalMilliseconds < tunnelTimeoutMilliseconds;

                        if (orig != IsOnline)
                        {
                            OnlineStatusChanged?.Invoke(this, new OnlineStatusEventArgs(IsOnline));
                        }
                    }

                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Program.Log($"{nameof(MonitorOnlineStatus)}: {ex.Message}");
            }
        }

        public override void Stop()
        {
            ReceiveQueue
                .Keys
                .ToList()
                .ForEach(key =>
                {
                    var teardownCommand = new TearDown(key);
                    SendQueue.Add(teardownCommand);

                    var receiveQueue = ReceiveQueue[key];
                    receiveQueue.CompleteAdding();
                    ReceiveQueue.Remove(key);
                });
        }

        public string WriteToFilename { get; } = writeToFilename;
        public int PurgeSizeInBytes { get; } = purgeSizeInBytes;
        public string ReadFromFilename { get; } = readFromFilename;
    }

    public class OnlineStatusEventArgs(bool isOnline)
    {
        public bool IsOnline { get; } = isOnline;
    }
}
