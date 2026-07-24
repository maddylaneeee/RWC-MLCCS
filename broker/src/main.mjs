import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { createBroker } from "../broker.mjs";

const configFlag = process.argv.indexOf("--config");
const configPath = resolve(
  configFlag >= 0 ? process.argv[configFlag + 1] : "broker/config/private.json",
);
const config = JSON.parse(await readFile(configPath, "utf8"));
const broker = createBroker(config);
const address = await broker.listen();
console.log(`CRC broker v2 listening at ws://${address.address}:${address.port}${config.listen.path}`);

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.once(signal, async () => {
    await broker.close();
    process.exit(0);
  });
}
