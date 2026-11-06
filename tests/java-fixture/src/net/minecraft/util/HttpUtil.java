package net.minecraft.util;
import java.net.ServerSocket; import java.io.IOException;
public class HttpUtil { public static int getAvailablePort() throws IOException { try (ServerSocket s = new ServerSocket(0)) { return s.getLocalPort(); } } }
