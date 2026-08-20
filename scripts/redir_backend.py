import http.server
import socketserver
import socket


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/login":
            # 模拟后端在 3xx 里返回内部主机 Location（网关需改写为公网入口）
            self.send_response(302)
            self.send_header("Location", "http://localhost:5200/dashboard")
            self.send_header("Content-Length", "0")
            self.end_headers()
        else:
            self.send_response(200)
            body = b"ok"
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    def log_message(self, *args):
        pass


class DualStackServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    address_family = socket.AF_INET6

    def server_bind(self):
        self.socket.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        super().server_bind()


if __name__ == "__main__":
    DualStackServer(("::", 5200), Handler).serve_forever()
