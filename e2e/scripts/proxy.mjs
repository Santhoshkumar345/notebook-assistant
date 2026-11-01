// Stands in for the platform's edge in the production-mode run: the thing a host puts in front of the app that ends TLS and says so in
// X-Forwarded-*. The browser talks to this on one port; this talks to the API on another, plain http, and claims the request was https.
// Like a real proxy it overwrites what the client sent in those headers instead of passing them on (otherwise a visitor could name their own address).
//
//   node proxy.mjs <listen-port> <target-port>
import { createServer, request } from 'node:http';

const [listenPort, targetPort] = process.argv.slice(2).map(Number);
if (!listenPort || !targetPort) {
  console.error('usage: node proxy.mjs <listen-port> <target-port>');
  process.exit(2);
}

createServer((incoming, outgoing) => {
  const headers = { ...incoming.headers, 'x-forwarded-proto': 'https', 'x-forwarded-for': incoming.socket.remoteAddress ?? '' };
  const upstream = request({ host: '127.0.0.1', port: targetPort, method: incoming.method, path: incoming.url, headers }, (reply) => {
    outgoing.writeHead(reply.statusCode ?? 502, reply.headers);
    reply.pipe(outgoing);
  });
  upstream.on('error', () => {
    if (!outgoing.headersSent) outgoing.writeHead(502, { 'content-type': 'text/plain' });
    outgoing.end('proxy: the API did not answer');
  });
  // A browser that walks away (Stop, closing the tab) must reach the API as a cancelled request, as it does through a real proxy; otherwise the
  // API would carry on, finish the model call and save an exchange nobody waited for.
  outgoing.on('close', () => {
    if (!outgoing.writableFinished) upstream.destroy();
  });
  incoming.pipe(upstream);
}).listen(listenPort, '127.0.0.1', () => console.log(`proxy ${listenPort} -> ${targetPort}`));
