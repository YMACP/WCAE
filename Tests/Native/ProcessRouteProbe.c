// Isolated native TCP client used only by the WinDivert route integration test.
#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
int main(int argc, char **argv)
{
    if (argc != 2) return 2;
    int port = atoi(argv[1]); if (port < 1024 || port > 65535) return 3;
    WSADATA data; if (WSAStartup(MAKEWORD(2,2), &data)) return 4;
    SOCKET s = socket(AF_INET, SOCK_STREAM, 0); if (s == INVALID_SOCKET) return 5;
    DWORD timeout = 5000;
    setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
    setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));
    struct sockaddr_in address = {0}; address.sin_family = AF_INET;
    address.sin_port = htons((unsigned short)port); address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (connect(s, (struct sockaddr*)&address, sizeof(address)) == SOCKET_ERROR) return 6;
    if (send(s, "PING", 4, 0) != 4) return 7;
    char response[32] = {0}; int length = recv(s, response, sizeof(response)-1, 0);
    if (length <= 0) return 8;
    printf("%s", response); fflush(stdout); closesocket(s); WSACleanup(); return 0;
}
