package net.minecraft.server;
import java.net.*; import java.io.*; import java.util.function.BooleanSupplier;
import net.minecraft.server.players.PlayerList;
import net.minecraft.world.level.GameType;
public class MinecraftServer {
 private final PlayerList players = new PlayerList();
 private boolean published;
 public void tickServer(BooleanSupplier value) { }
 public PlayerList getPlayerList() { return players; }
 public GameType getDefaultGameType() { return GameType.SURVIVAL; }
 public WorldData getWorldData() { return new WorldData(); }
 public boolean isPublished() { return published; }
 public boolean publishServer(GameType mode, boolean cheats, int port) throws IOException {
  listen(port); published = true; return true;
 }
 public static class WorldData { public boolean getAllowCommands() { return false; } }
 public static ServerSocket listen(int port) throws IOException {
  ServerSocket listener = new ServerSocket(port);
  Thread thread = new Thread(() -> {
   while (!listener.isClosed()) {
    try { Socket socket = listener.accept();
     Thread connection = new Thread(() -> { try (Socket s = socket) {
      if (s.getInputStream().read() == 254) { s.getOutputStream().write(255); s.getOutputStream().flush(); }
     } catch (IOException ignored) { } }); connection.setDaemon(true); connection.start();
    } catch (IOException ignored) { }
   }
  }); thread.setDaemon(true); thread.start(); return listener;
 }
}
