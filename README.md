# IPvX-PortForwarder

## 功能
IPvX-PortForwarder 是一个支持 IPv4 和 IPv6 的端口转发工具，可在指定的监听地址和端口上，将收到的 TCP 和 UDP 请求转发到目标地址和端口。支持多路转发配置。适用于需要跨协议、跨地址族进行网络通信的场景(例如让不支持IPv6的单机游戏通过IPv6公网进行联机)。

## 配置
运行程序时，将读取当前目录下的 `config.json` 配置文件。如果文件不存在，程序会自动生成一个示例配置文件。

## 配置文件格式
```json
{
  "portForwardConfigs": [
    {
      "listenIpAddress": "监听的 IP 地址",
      "listenPort": 监听的端口号,
      "forwardIpAddress": "目标 IP 地址",
      "forwardPort": 目标端口号,
      "udpSessionTimeout": UDP 会话超时时间（秒，可选，默认 120）,
      "tcpConnectHandleThreadCount": TCP 连接处理线程数（可选，默认 2）,
      "udpClientCleanInterval": UDP 会话清理间隔（秒，可选，默认 60）
    }
    // 可以添加多个转发配置
  ]
}
```

## 配置项说明
- **ListenIpAddress**：本地监听的 IP 地址，可使用 IPv4 或 IPv6，例如 "127.0.0.1" 或 "[::1]"。IPv6地址应使用`[]`括起来。
- **ListenPort**：本地监听的端口号。
- **ForwardIpAddress**：转发到的目标 IP 地址。可使用 IPv4 或 IPv6。
- **ForwardPort**：转发到的目标端口号。
- **UdpSessionTimeout（可选）**：UDP 会话超时时间，单位为秒，默认值为 120。超过这个时间没有发送数据的UDP客户端将会被清理。被清理后将无法收到来自Forward端的响应。
- **TcpConnectHandleThreadCount（可选）**：TCP 连接处理线程数，默认值为 2。
- **UdpClientCleanInterval（可选）**：UDP 会话清理间隔，单位为秒，默认值为 60。清理UDP客户端将会在这个间隔后进行。

## 示例配置
```json
{
  "portForwardConfigs": [
    {
      "listenIpAddress": "127.0.0.1",
      "listenPort": 12001,
      "forwardIpAddress": "192.168.1.100",
      "forwardPort": 8080
    },
    {
      "listenIpAddress": "[::1]",
      "listenPort": 443,
      "forwardIpAddress": "20.205.243.166",
      "forwardPort": 443,
      "udpSessionTimeout": 180,
      "tcpConnectHandleThreadCount": 4,
      "udpClientCleanInterval": 90
    }
  ]
}
```
程序将根据配置文件中的内容开始监听指定的端口并转发请求。

## 注意事项
1. 确保监听的端口未被其他程序占用。
2. 如果监听特权端口（如 80 或 443），需要以具有相应权限的用户身份运行程序。
3. 修改配置文件后，需要重新启动程序生效。
4. 若用于游戏联机，需注意主机和客户端都需要进行转发。主机端将游戏端口转发到IPv6公网上，客户端将IPv6的游戏端口转发到本地。