import java.net.*;
import net.minecraft.server.MinecraftServer;
// Controlled JVM fixture, not a Minecraft implementation or interoperability test.
public class Fixture {
 public static void main(String[] args) throws Exception {
  ServerSocket tcp = MinecraftServer.listen(0);
  DatagramSocket udp = new DatagramSocket(tcp.getLocalPort());
  System.out.println("PORT=" + tcp.getLocalPort()); System.out.flush();
  MinecraftServer server = new MinecraftServer();
  while (true) { server.tickServer(() -> true); Thread.sleep(50); }
 }
}
