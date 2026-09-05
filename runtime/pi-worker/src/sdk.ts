/**
 * Official SDK adapter (SPEC.md section 25.1).
 *
 * The only module allowed to import `@earendil-works/pi-coding-agent`.
 * Builds a technically restricted root session through `createAgentSession`
 * with `ModelRuntime.create`, `SessionManager.create` (persistence under the
 * supplied `agentDir`), and `SettingsManager.inMemory` (no user settings
 * I/O), with the read-only built-ins plus the fifteen orchestration tools.
 */
import {
  DefaultResourceLoader,
  ModelRuntime,
  SessionManager,
  SettingsManager,
  createAgentSession,
  defineTool,
  type ToolDefinition,
} from "@earendil-works/pi-coding-agent";
import { Type, type TSchema } from "typebox";
import type { PiSessionLike, PiSessionFactory, RootSessionConfig } from "./pisession.ts";
import {
  ROOT_BUILTIN_TOOLS,
  ROOT_SYSTEM_PROMPT,
  ROOT_TOOL_NAMES,
  buildRootTools,
  type RootTool,
} from "./rootTools.ts";
import {
  CHILD_BUILTIN_TOOLS,
  CHILD_SYSTEM_PROMPT,
  CHILD_TOOL_NAMES,
  buildChildTools,
} from "./childTools.ts";

/** Worker→node request transport backed by the correlated request broker. */
export type NodeRequest = (
  sessionId: string,
  type: string,
  payload: Record<string, unknown>,
) => Promise<unknown>;

/** Convert a declarative property spec into TypeBox schemas. */
function typeBoxProperties(properties: RootTool["properties"]): Record<string, TSchema> {
  const out: Record<string, TSchema> = {};
  for (const [name, spec] of Object.entries(properties)) {
    const options = { description: spec.description };
    switch (spec.type) {
      case "number":
        out[name] = Type.Number(options);
        break;
      case "boolean":
        out[name] = Type.Boolean(options);
        break;
      case "array":
        out[name] = Type.Array(Type.String(), options);
        break;
      default:
        out[name] = Type.String(options);
    }
  }
  return out;
}

/** Adapt a declarative root tool to an SDK custom-tool definition. */
function adaptTool(tool: RootTool): ToolDefinition {
  return defineTool({
    name: tool.name,
    label: tool.label,
    description: tool.description,
    parameters: Type.Object(typeBoxProperties(tool.properties)),
    execute: async (_toolCallId: string, params: Record<string, unknown>) => {
      // tool.execute round-trips to the node and returns bounded JSON text.
      const text = await tool.execute(params);
      return {
        content: [{ type: "text", text }],
        details: {},
      };
    },
  });
}

/**
 * Production factory. Each call creates one SDK AgentSession: one worker
 * process per session, persisted under `config.agentDir`. Root mode gets the
 * allowlist `read grep find ls` + 15 orchestration round-trips; child mode
 * gets the same read-only built-ins plus the reservation-enforced child
 * tools (SPEC.md sections 18.1, 25.2, 25.3). Neither mode receives the
 * default unrestricted edit/write/shell tools.
 */
export function createSdkSessionFactory(nodeRequest: NodeRequest): PiSessionFactory {
  return {
    async create(config: RootSessionConfig): Promise<PiSessionLike> {
      const isChild = config.mode === "child";
      const buildTools = isChild ? buildChildTools : buildRootTools;
      const builtins = isChild ? CHILD_BUILTIN_TOOLS : ROOT_BUILTIN_TOOLS;
      const customNames = isChild ? CHILD_TOOL_NAMES : ROOT_TOOL_NAMES;
      const defaultPrompt = isChild ? CHILD_SYSTEM_PROMPT : ROOT_SYSTEM_PROMPT;
      const tools = buildTools(async (type, payload) =>
        nodeRequest(config.sessionId, type, payload),
      );
      const modelRuntime = await ModelRuntime.create();
      const settingsManager = SettingsManager.inMemory();
      const resourceLoader = new DefaultResourceLoader({
        cwd: config.cwd,
        agentDir: config.agentDir,
        settingsManager,
        systemPromptOverride: () => config.systemPrompt ?? defaultPrompt,
      });
      await resourceLoader.reload();
      const { session } = await createAgentSession({
        cwd: config.cwd,
        agentDir: config.agentDir,
        modelRuntime,
        tools: [...builtins, ...customNames],
        customTools: tools.map(adaptTool),
        resourceLoader,
        sessionManager: SessionManager.create(config.cwd),
        settingsManager,
      });
      return session as unknown as PiSessionLike;
    },
  };
}
