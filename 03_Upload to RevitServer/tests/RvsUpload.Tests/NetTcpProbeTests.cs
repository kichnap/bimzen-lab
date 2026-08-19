using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;

namespace RvsUpload.Tests
{
    /// <summary>
    /// Пробник канала передачи моделей. Проверяем на поддельном сервере:
    /// живого Revit Server для этого не нужно, а поведение важно ровно
    /// в тех случаях, которые на живом сервере воспроизвести труднее всего —
    /// служба отсутствует, служба молчит, порт закрыт.
    /// </summary>
    public class NetTcpProbeTests
    {
        /// <summary>Одноразовый слушатель: принимает соединение и отвечает заданными байтами.</summary>
        private sealed class FakeServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public byte[] Received = new byte[0];

            public FakeServer(byte[] reply, bool staySilent = false)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _thread = new Thread(() =>
                {
                    try
                    {
                        using (var c = _listener.AcceptTcpClient())
                        {
                            var s = c.GetStream();
                            var buf = new byte[1024];
                            s.ReadTimeout = 3000;
                            var n = s.Read(buf, 0, buf.Length);
                            Received = new byte[Math.Max(n, 0)];
                            if (n > 0) Array.Copy(buf, Received, n);

                            if (staySilent) { Thread.Sleep(1500); return; }
                            s.Write(reply, 0, reply.Length);
                            s.Flush();
                            Thread.Sleep(150);
                        }
                    }
                    catch { /* тест закончился раньше */ }
                }) { IsBackground = true };
                _thread.Start();
            }

            public void Dispose()
            {
                try { _listener.Stop(); } catch { }
            }
        }

        private static byte[] Fault(string uri)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x08);
                var bytes = Encoding.UTF8.GetBytes(uri);
                NetTcpProbe.WriteVarInt(ms, bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
                return ms.ToArray();
            }
        }

        [Fact]
        public void Ok_WhenServiceAcknowledges()
        {
            using (var srv = new FakeServer(new byte[] { 0x0B }))
            {
                var r = NetTcpProbe.Probe("127.0.0.1", 2024, 3000, srv.Port);

                Assert.Equal(NetTcpProbe.Outcome.Ok, r.Outcome);
                Assert.True(r.IsOk);
            }
        }

        [Fact]
        public void ServiceUnavailable_WhenServerFaults()
        {
            var uri = "http://schemas.microsoft.com/ws/2006/05/framing/faults/EndpointUnavailable";
            using (var srv = new FakeServer(Fault(uri)))
            {
                var r = NetTcpProbe.Probe("127.0.0.1", 2024, 3000, srv.Port);

                Assert.Equal(NetTcpProbe.Outcome.ServiceUnavailable, r.Outcome);
                // Человеку показываем последнюю часть, а не весь URI схемы.
                Assert.Equal("EndpointUnavailable", r.Detail);
            }
        }

        [Fact]
        public void NoAnswer_WhenServerStaysSilent()
        {
            using (var srv = new FakeServer(new byte[0], staySilent: true))
            {
                var r = NetTcpProbe.Probe("127.0.0.1", 2024, 700, srv.Port);

                Assert.NotEqual(NetTcpProbe.Outcome.Ok, r.Outcome);
                Assert.False(r.IsOk);
            }
        }

        [Fact]
        public void NoConnection_WhenNothingListens()
        {
            // Порт, на котором заведомо никто не слушает.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var free = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var r = NetTcpProbe.Probe("127.0.0.1", 2024, 1500, free);

            Assert.Equal(NetTcpProbe.Outcome.NoConnection, r.Outcome);
        }

        [Fact]
        public void Preamble_NamesTheVersionedService()
        {
            using (var srv = new FakeServer(new byte[] { 0x0B }))
            {
                NetTcpProbe.Probe("127.0.0.1", 2021, 3000, srv.Port);
                Thread.Sleep(200);

                var text = Encoding.UTF8.GetString(srv.Received);
                Assert.Contains("net.tcp://127.0.0.1/ModelService2021/ModelService.svc/tcpbuffer", text);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(127)]
        [InlineData(128)]
        [InlineData(300)]
        [InlineData(16384)]
        public void VarInt_RoundTrips(int value)
        {
            using (var ms = new MemoryStream())
            {
                NetTcpProbe.WriteVarInt(ms, value);
                var bytes = ms.ToArray();

                // Читаем тем же способом, что и разбор отказа.
                var i = 0; var result = 0; var shift = 0; byte b;
                do { b = bytes[i++]; result |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);

                Assert.Equal(value, result);
            }
        }


        [Fact]
        public void Retries_DoNotRepeatWhenServiceIsMissing()
        {
            // Отсутствие службы — окончательный ответ, повторять нечего.
            var uri = "http://schemas.microsoft.com/ws/2006/05/framing/faults/EndpointUnavailable";
            using (var srv = new FakeServer(Fault(uri)))
            {
                var r = NetTcpProbe.ProbeWithRetries("127.0.0.1", 2024, 3, 3000, srv.Port);

                Assert.Equal(NetTcpProbe.Outcome.ServiceUnavailable, r.Outcome);
            }
        }

        [Fact]
        public void Retries_GiveUpAndReportLastOutcome()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var free = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var r = NetTcpProbe.ProbeWithRetries("127.0.0.1", 2024, 2, 800, free);

            Assert.Equal(NetTcpProbe.Outcome.NoConnection, r.Outcome);
        }

        [Fact]
        public void EmptyHost_Throws()
            => Assert.Throws<ArgumentException>(() => NetTcpProbe.Probe("", 2024));
    }
}
