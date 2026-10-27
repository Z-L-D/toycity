/// <reference types="vite/client" />
declare module '*.proto?raw' {
  const text: string;
  export default text;
}
