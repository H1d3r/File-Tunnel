﻿using ft.CLI;
using ft.Listeners;
using ft.Streams;
using ft.Utilities;
using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

namespace ft
{
    public class Program
    {
        const string PROGRAM_NAME = "File Tunnel";
        const string VERSION = "2.0.2";


        static int connectionId = 0;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Options))]
        public static void Main(string[] args)
        {
            Log($"{PROGRAM_NAME} {VERSION}");

            Parser.Default.ParseArguments<Options>(args)
               .WithParsed(o =>
               {
                   if (o.PrintVersion)
                   {
                       Environment.Exit(0);
                   }

                   StreamEstablisher? listener = null;

                   if (!string.IsNullOrEmpty(o.TcpListenTo) || !string.IsNullOrEmpty(o.UdpListenTo))
                   {
                       if (!string.IsNullOrEmpty(o.TcpListenTo)) listener = new TcpServer(o.TcpListenTo);
                       if (!string.IsNullOrEmpty(o.UdpListenTo)) listener = new UdpServer(o.UdpListenTo);

                       if (listener == null)
                       {
                           Log("No listener specified by args");
                           return;
                       }

                       var listenToStr = o.TcpListenTo;
                       if (string.IsNullOrEmpty(listenToStr)) listenToStr = o.UdpListenTo;

                       if (listenToStr != null && !listenToStr.IsValidEndpoint())
                       {
                           Log($"Invalid endpoint specified: {listenToStr}");
                           Log($"Please specify IP:Port or Hostname:Port or [IPV6]:Port");
                           return;
                       }

                       Log($"Will listen to: {listenToStr}");
                       Log($"and forward to: {o.WriteTo}");
                       if (!string.IsNullOrEmpty(o.ReadFrom)) Log($"and read responses from: {o.ReadFrom}");

                       if (string.IsNullOrEmpty(o.ReadFrom)) throw new Exception("Please supply --read");
                       if (string.IsNullOrEmpty(o.WriteTo)) throw new Exception("Please supply --write");

                       var sharedFileManager = new SharedFileManager(o.ReadFrom, o.WriteTo.Trim(), o.PurgeSizeInBytes, o.TunnelTimeoutMilliseconds);

                       sharedFileManager.OnlineStatusChanged += (sender, args) =>
                       {
                           if (args.IsOnline)
                           {
                               listener.Start();
                           }
                           else
                           {
                               listener.Stop();
                           }
                       };

                       var relayStreamCreator = new Func<Stream>(() =>
                       {
                           var cId = connectionId++;
                           var sharedFileStream = new SharedFileStream(sharedFileManager, cId);
                           sharedFileStream.EstablishConnection();
                           return sharedFileStream;
                       });

                       if (listener == null) return;

                       listener.StreamEstablished += (sender, stream) =>
                       {
                           var secondaryStream = relayStreamCreator();

                           var relay1 = new Relay(stream, secondaryStream, o.PurgeSizeInBytes, o.ReadDurationMillis);
                           var relay2 = new Relay(secondaryStream, stream, o.PurgeSizeInBytes, o.ReadDurationMillis);

                           void tearDown()
                           {
                               relay1.Stop();
                               relay2.Stop();
                           }

                           relay1.RelayFinished += (s, a) => tearDown();
                           relay2.RelayFinished += (s, a) => tearDown();
                       };

                       sharedFileManager.Start();
                   }

                   if (!string.IsNullOrEmpty(o.TcpConnectTo) || !string.IsNullOrEmpty(o.UdpSendTo))
                   {
                       if (string.IsNullOrEmpty(o.ReadFrom)) throw new Exception("Please supply --read");
                       if (string.IsNullOrEmpty(o.WriteTo)) throw new Exception("Please supply --write");

                       var sharedFileManager = new SharedFileManager(o.ReadFrom, o.WriteTo, o.PurgeSizeInBytes, o.TunnelTimeoutMilliseconds);

                       if (!string.IsNullOrEmpty(o.UdpSendTo) && string.IsNullOrEmpty(o.UdpSendFrom))
                       {
                           Log($"Please specify a (local) sender address to use for sending data, using the --udp-send-from argument.");
                           Environment.Exit(1);
                       }

                       var forwardToStr = o.TcpConnectTo;
                       if (string.IsNullOrEmpty(forwardToStr)) forwardToStr = o.UdpSendTo;

                       if (forwardToStr != null && !forwardToStr.IsValidEndpoint())
                       {
                           Log($"Invalid endpoint specified: {forwardToStr}");
                           Log($"Please specify IP:Port or Hostname:Port or [IPV6]:Port");
                           return;
                       }

                       Log($"Will listen to: {o.ReadFrom}");
                       Log($"and forward to: {forwardToStr}");
                       if (!string.IsNullOrEmpty(o.WriteTo)) Log($"and when they respond, will write the response to: {o.WriteTo}");

                       sharedFileManager.OnlineStatusChanged += (sender, args) =>
                       {
                           if (!args.IsOnline)
                           {
                               sharedFileManager.Stop();
                           }
                       };

                       sharedFileManager.StreamEstablished += (sender, stream) =>
                       {
                           if (!string.IsNullOrEmpty(o.TcpConnectTo))
                           {
                               var tcpClient = new TcpClient();

                               if (IPEndPoint.TryParse(o.TcpConnectTo, out var connectToEndpoint))
                               {
                                   tcpClient.Connect(connectToEndpoint);
                               }
                               else
                               {
                                   var tokens = o.TcpConnectTo.Split([":"], StringSplitOptions.None);
                                   tcpClient.Connect(tokens[0], int.Parse(tokens[1]));
                               }

                               Log($"Connected to {o.TcpConnectTo}");

                               var relay1 = new Relay(tcpClient.GetStream(), stream, o.PurgeSizeInBytes, o.ReadDurationMillis);
                               var relay2 = new Relay(stream, tcpClient.GetStream(), o.PurgeSizeInBytes, o.ReadDurationMillis);

                               void tearDown()
                               {
                                   relay1.Stop();
                                   relay2.Stop();
                               }

                               relay1.RelayFinished += (s, a) => tearDown();
                               relay2.RelayFinished += (s, a) => tearDown();
                           }

                           if (!string.IsNullOrEmpty(o.UdpSendFrom) && !string.IsNullOrEmpty(o.UdpSendTo))
                           {
                               var sendFromEndpoint = IPEndPoint.Parse(o.UdpSendFrom);
                               var sendToEndpoint = IPEndPoint.Parse(o.UdpSendTo);

                               var udpClient = new UdpClient();
                               udpClient.Client.Bind(sendFromEndpoint);

                               var udpStream = new UdpStream(udpClient, sendToEndpoint);

                               Log($"Will send data to {o.UdpSendTo} from {o.UdpListenTo}");

                               var relay1 = new Relay(udpStream, stream, o.PurgeSizeInBytes, o.ReadDurationMillis);
                               var relay2 = new Relay(stream, udpStream, o.PurgeSizeInBytes, o.ReadDurationMillis);

                               void tearDown()
                               {
                                   relay1.Stop();
                                   relay2.Stop();
                               }

                               relay1.RelayFinished += (s, a) => tearDown();
                               relay2.RelayFinished += (s, a) => tearDown();
                           }
                       };

                       sharedFileManager.Start();
                   }
               });

            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                }
                catch
                {
                    break;
                }
            }
        }

        public static readonly ConsoleColor OriginalConsoleColour = Console.ForegroundColor;
        public static readonly Random Random = new();

        public static readonly object ConsoleOutputLock = new();

        public static void Log(string str, ConsoleColor? color = null)
        {
            lock (ConsoleOutputLock)
            {
                // Change color if specified
                if (color.HasValue)
                {
                    Console.ForegroundColor = color.Value;
                }

                Console.WriteLine($"{DateTime.Now}: {str}");

                // Reset to original color
                Console.ForegroundColor = OriginalConsoleColour;
            }
        }
    }
}
