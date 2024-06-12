using Microsoft.VisualStudio.TestPlatform.TestHost;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using ft_tests.Utilities;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text;

namespace ft_tests
{
    [DoNotParallelize]
    [TestClass]
    public partial class TcpUnitTests
    {
        [TestMethod]
        public void SingleConnection_HalfDuplex()
        {
            TestTransfer(50 * 1024 * 1024, "127.0.0.1:5001", "127.0.0.1:8001", Path.GetTempFileName(), Path.GetTempFileName(), false, 1);
        }

        [TestMethod]
        public void SingleConnection_FullDuplex()
        {
            TestTransfer(50 * 1024 * 1024, "127.0.0.1:5001", "127.0.0.1:8001", Path.GetTempFileName(), Path.GetTempFileName(), true, 1);
        }

        [TestMethod]
        public void MultipleConnections_FullDuplex()
        {
            TestTransfer(50 * 1024 * 1024, "127.0.0.1:5001", "127.0.0.1:8001", Path.GetTempFileName(), Path.GetTempFileName(), true, 10);
        }

        [TestMethod]
        public void ServerSendsFirst()
        {
            var listenPoint = "127.0.0.1:5000";
            var connectPoint = "127.0.0.1:6000";

            var writeFilename = Path.GetTempFileName();
            var readFilename = Path.GetTempFileName();


            var listenThread = new Thread(() =>
            {
                var listenArgsString = $@"--tcp-listen {listenPoint} --write ""{writeFilename}"" --read ""{readFilename}""";

                var listenArgs = StringUtility.CommandLineToArgs(listenArgsString);
                ft.Program.Main(listenArgs);
            });
            listenThread.Start();

            var forwardThread = new Thread(() =>
            {
                var forwardArgsString = $@"--read ""{writeFilename}"" --tcp-connect {connectPoint} --write ""{readFilename}""";

                var forwardArgs = StringUtility.CommandLineToArgs(forwardArgsString);
                ft.Program.Main(forwardArgs);
            });
            forwardThread.Start();


            var ultimateDestination = new TcpListener(IPEndPoint.Parse(connectPoint));
            ultimateDestination.Start();
            var ultimateDestinationAcceptCT = new CancellationTokenSource();
            var ultimateDestinationClients = new BlockingCollection<TcpClient>();

            var bytesToSend = Encoding.ASCII.GetBytes("hello");

            Task.Factory.StartNew(() =>
            {
                var client = ultimateDestination.AcceptTcpClient();

                client.GetStream().Write(bytesToSend);

            }, TaskCreationOptions.LongRunning);


            var originClient = new TcpClient();
            var startTime = DateTime.Now;
            while (true)
            {
                var duration = DateTime.Now - startTime;
                if (duration.TotalSeconds > 10)
                {
                    throw new Exception("Could not connect");
                }
                try
                {
                    originClient.Connect(IPEndPoint.Parse(listenPoint));
                }
                catch
                {
                    Thread.Sleep(200);
                    continue;
                }
                break;
            }

            var buffer = new byte[1024];
            var bytesRead = originClient.GetStream().Read(buffer);

            var receivedMatchesSent = bytesToSend.SequenceEqual(buffer.Take(bytesRead).ToArray());

            Assert.IsTrue(receivedMatchesSent, $"Received buffer does not match sent buffer");
        }

        public static void TestTransfer(int bytesToSend, string listenPoint, string connectPoint, string writeFilename, string readFilename, bool fullDuplex, int connections)
        {
            var listenThread = new Thread(() =>
            {
                var listenArgsString = $@"--tcp-listen {listenPoint} --write ""{writeFilename}"" --read ""{readFilename}""";

                var listenArgs = StringUtility.CommandLineToArgs(listenArgsString);
                ft.Program.Main(listenArgs);
            });
            listenThread.Start();

            var forwardThread = new Thread(() =>
            {
                var forwardArgsString = $@"--read ""{writeFilename}"" --tcp-connect {connectPoint} --write ""{readFilename}""";

                var forwardArgs = StringUtility.CommandLineToArgs(forwardArgsString);
                ft.Program.Main(forwardArgs);
            });
            forwardThread.Start();

            var ultimateDestination = new TcpListener(IPEndPoint.Parse(connectPoint));
            ultimateDestination.Start();
            var ultimateDestinationAcceptCT = new CancellationTokenSource();
            var ultimateDestinationClients = new BlockingCollection<TcpClient>();

            Task.Factory.StartNew(() =>
            {
                while (!ultimateDestinationAcceptCT.IsCancellationRequested)
                {
                    try
                    {
                        var client = ultimateDestination.AcceptTcpClient();
                        ultimateDestinationClients.Add(client);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, TaskCreationOptions.LongRunning);

            Enumerable
                .Range(0, connections)
                .Select(connection =>
                {
                    var originClient = new TcpClient();

                    var startTime = DateTime.Now;

                    while (true)
                    {
                        var duration = DateTime.Now - startTime;
                        if (duration.TotalSeconds > 10)
                        {
                            throw new Exception("Could not connect");
                        }

                        try
                        {
                            originClient.Connect(IPEndPoint.Parse(listenPoint));
                        }
                        catch
                        {
                            Thread.Sleep(200);
                            continue;
                        }

                        break;
                    }

                    var ultimateDestinationClient = ultimateDestinationClients.GetConsumingEnumerable().First();
                    Debug.WriteLine($"Accepted connection from: {ultimateDestinationClient.Client.RemoteEndPoint}");

                    return new
                    {
                        OriginClient = originClient,
                        UltimateDestinationClient = ultimateDestinationClient
                    };
                })
                .ToList()
                .AsParallel()
                .WithDegreeOfParallelism(connections)
                .ForAll(pair =>
                {
                    var toSend = new byte[bytesToSend];
                    var random = new Random();
                    random.NextBytes(toSend);

                    var tests = new[]
                    {
                            new Action(() => TestDirection("Forward", pair.OriginClient, pair.UltimateDestinationClient, toSend)),
                            new Action(() => TestDirection("Reverse", pair.UltimateDestinationClient, pair.OriginClient, toSend)),
                        };

                    if (fullDuplex)
                    {
                        var testTasks = tests
                                            .ToList()
                                            .Select(test => Task.Factory.StartNew(test, TaskCreationOptions.LongRunning))
                                            .ToArray();

                        Task.WaitAll(testTasks);
                    }
                    else
                    {
                        foreach (var test in tests)
                        {
                            test();
                        }
                    }
                });


            ultimateDestinationAcceptCT.Cancel();
            ultimateDestination.Stop();

            listenThread.Interrupt();
            listenThread.Join();

            forwardThread.Interrupt();
            forwardThread.Join();

            //File.Delete(readFilename);
            //File.Delete(writeFilename);
        }

        static void TestDirection(string direction, TcpClient sender, TcpClient receiver, byte[] toSend)
        {
            sender.GetStream().Write(toSend, 0, toSend.Length);

            var received = new byte[toSend.Length];

            int totalRead = 0;
            while (totalRead < toSend.Length)
            {
                var toRead = Math.Min(1024 * 1024, received.Length - totalRead);
                var read = receiver.GetStream().Read(received, totalRead, toRead);
                totalRead += read;
            }

            var receivedSuccessfully = received.SequenceEqual(toSend);
            Assert.IsTrue(receivedSuccessfully, $"[{direction}] Received buffer does not match sent buffer");
        }
    }
}