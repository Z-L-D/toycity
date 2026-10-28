import protobuf from 'protobufjs';
import protoText from '../../../protocol/toycity.proto?raw';
import transportText from '../../../protocol/transport.proto?raw';

// The .proto file is the single source of truth; the client parses it at startup.
const root = protobuf.parse(protoText, { keepCase: false }).root;
const ClientMessage = root.lookupType('toycity.v0.ClientMessage');
const ServerMessage = root.lookupType('toycity.v0.ServerMessage');

export type Msg = Record<string, any>;

// The bus company's own messages travel as bytes inside the city's; only its panel reads them.
const transportRoot = protobuf.parse(transportText, { keepCase: false }).root;
const TransportView = transportRoot.lookupType('toycity.transport.v1.View');
const TransportCommand = transportRoot.lookupType('toycity.transport.v1.Command');

export function decodeTransportView(bytes: Uint8Array): Msg {
  return TransportView.toObject(TransportView.decode(bytes), { longs: Number, enums: Number, defaults: true, arrays: true }) as Msg;
}

export function encodeTransportCommand(cmd: Msg): Uint8Array {
  return TransportCommand.encode(TransportCommand.fromObject(cmd)).finish();
}

export interface ServerEvents {
  onMessage(kind: string, body: Msg, replyTo: number): void;
  onStatus(connected: boolean): void;
}

/** WebSocket connection to the ToyCity server, with automatic reconnect. */
export class Connection {
  private ws: WebSocket | null = null;
  private seq = 1;
  private retry = 500;

  constructor(private readonly events: ServerEvents) {
    this.open();
  }

  private url(): string {
    const params = new URLSearchParams(location.search);
    const server = params.get('server');
    if (server) return server;
    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    return `${scheme}://${location.host}/ws`;
  }

  private open() {
    const ws = new WebSocket(this.url());
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => {
      this.retry = 500;
      this.events.onStatus(true);
      this.send({ hello: { clientName: 'toycity-web', protocolVersion: 0 } });
    };
    ws.onclose = () => {
      this.events.onStatus(false);
      this.ws = null;
      setTimeout(() => this.open(), this.retry);
      this.retry = Math.min(this.retry * 2, 8000);
    };
    ws.onmessage = (ev) => {
      const decoded = ServerMessage.decode(new Uint8Array(ev.data as ArrayBuffer));
      const obj = ServerMessage.toObject(decoded, { longs: Number, enums: Number, defaults: true, oneofs: true, arrays: true }) as Msg;
      const kind = obj.body as string;
      if (kind) this.events.onMessage(kind, obj[kind], obj.replyTo ?? 0);
    };
    this.ws = ws;
  }

  /** Sends a ClientMessage body such as `{ action: { path: {...} } }`. Returns its sequence number. */
  send(body: Msg): number {
    const seq = this.seq++;
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return seq;
    const err = ClientMessage.verify({ seq, ...body });
    if (err) {
      console.error('bad client message', err, body);
      return seq;
    }
    const bytes = ClientMessage.encode(ClientMessage.fromObject({ seq, ...body })).finish();
    this.ws.send(bytes);
    return seq;
  }

  action(action: Msg) {
    return this.send({ action });
  }
}
