import protobuf from 'protobufjs';
import protoText from '../../../protocol/toycity.proto?raw';
import transportText from '../../../protocol/transport.proto?raw';
import emergencyText from '../../../protocol/emergency.proto?raw';

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

// The emergency services' messages, the same way.
const emergencyRoot = protobuf.parse(emergencyText, { keepCase: false }).root;
const EmergencyView = emergencyRoot.lookupType('toycity.emergency.v1.View');
const EmergencyCommand = emergencyRoot.lookupType('toycity.emergency.v1.Command');

export function decodeEmergencyView(bytes: Uint8Array): Msg {
  return EmergencyView.toObject(EmergencyView.decode(bytes), { longs: Number, enums: Number, defaults: true, arrays: true }) as Msg;
}

export function encodeEmergencyCommand(cmd: Msg): Uint8Array {
  return EmergencyCommand.encode(EmergencyCommand.fromObject(cmd)).finish();
}

export interface ServerEvents {
  onMessage(kind: string, body: Msg, replyTo: number): void;
  onStatus(connected: boolean): void;
  /** The server wants a name and password (with why the last try failed, if it did). */
  onSignIn(welcome: Msg, error: string): void;
}

/**
 * WebSocket connection to the ToyCity server, with automatic reconnect. A dedicated server asks players to sign in;
 * the token it gives back is kept in this browser per server, so a reload or reconnect signs in by itself.
 */
export class Connection {
  private ws: WebSocket | null = null;
  private seq = 1;
  private retry = 500;
  private signInSeq = 0;
  private welcome: Msg = {};
  /** Who this connection plays as, once the server has said. */
  player = '';
  /** May save, load, found a city and change the speed. */
  admin = false;
  /** A dedicated server: players sign in, and seats go to the computer while their owner is away. */
  dedicated = false;

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
      if (!kind) return;
      const body = obj[kind];
      if (kind === 'welcome') {
        this.welcome = body;
        this.dedicated = body.signInRequired;
        this.player = body.player;
        this.admin = body.admin;
        if (body.signInRequired && !body.player) {
          const token = this.storedToken();
          if (token) this.sendSignIn({ token });
          else this.events.onSignIn(body, '');
        }
      } else if (kind === 'signedIn') {
        this.player = body.name;
        this.admin = body.admin;
        this.storeToken(body.token);
      } else if (kind === 'error' && obj.replyTo && obj.replyTo === this.signInSeq) {
        this.storeToken('');
        this.events.onSignIn(this.welcome, body.message);
        return;
      }
      this.events.onMessage(kind, body, obj.replyTo ?? 0);
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

  /** Signs in, or makes the account first when create is set. */
  signIn(name: string, password: string, create: boolean, inviteCode: string) {
    this.sendSignIn({ name, password, create, inviteCode });
  }

  private sendSignIn(signIn: Msg) {
    this.signInSeq = this.send({ signIn });
  }

  private tokenKey() {
    return `toycity-token:${this.url()}`;
  }

  private storedToken(): string {
    try { return localStorage.getItem(this.tokenKey()) ?? ''; } catch { return ''; }
  }

  private storeToken(token: string) {
    try {
      if (token) localStorage.setItem(this.tokenKey(), token);
      else localStorage.removeItem(this.tokenKey());
    } catch { /* private window: sign in each time */ }
  }

  action(action: Msg) {
    return this.send({ action });
  }
}
