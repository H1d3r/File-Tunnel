# File Tunnel

Tunnel TCP connections through a file.

<br />

## Download
Portable executables for Windows and Linux can be found over in the [releases](https://github.com/fiddyschmitt/file_tunnel/releases) section.

<br />

## Example 1 - Bypassing a firewall

![ft_fw](img/ft_fw.png?raw=true "Bypass")

You'd like to connect from Host A to Host B, but a firewall is in the way. But both hosts have access to a shared folder.

### Host A
``ft.exe --tcp-listen 127.0.0.1:5000 --write "\\server\share\1.dat" --read "\\server\share\2.dat"``

### Host B
``ft.exe --read "\\server\share\1.dat" --tcp-connect 127.0.0.1:3389 --write "\\server\share\2.dat"``

Now on Host A, configure the client to connect to: ``127.0.0.1:5000``

<br />
<br />
<br />

## Example 2 - Tunnel TCP through RDP (similar to SSH tunnel)

You'd like to connect to a remote service (eg. ``192.168.1.50:8888``), but only have access to Host B using RDP.

### Host A
``ft.exe --tcp-listen 127.0.0.1:5000 --write "C:\Temp\1.dat" --read "C:\Temp\2.dat"``

Run an RDP client and ensure local drives are shared as shown [here](https://github.com/fiddyschmitt/file_tunnel/assets/15338956/eb890310-47f5-4b46-9f74-471ec1735450).

Connect to Host B.

### Host B
``ft.exe --read "\\tsclient\c\Temp\1.dat" --tcp-connect 192.168.1.50:8888 --write "\\tsclient\c\Temp\2.dat"``

Now on Host A, you can connect to `127.0.0.1:5000` and it will be forwarded to `192.168.1.50:8888`

<br />
<br />
<br />

## How does it work?
The program starts a TCP listener, and when a connection is received it writes the TCP data into a file. This same file is read by the counterpart program, which establishes a TCP connection and onforwards the TCP data.
To avoid the shared file growing indefinitely, it is purged whenever it gets larger than 10 MB.
