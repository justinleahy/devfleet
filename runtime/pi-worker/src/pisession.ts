/**
 * Session factory seam (SPEC.md section 25.1).
 *
 * The worker talks to Pi exclusively through these structural interfaces so
 * behavioral tests can inject deterministic fakes while production code uses
 * the official SDK (see `sdk.ts` for the pinned `@earendil-works/pi-coding-agent`
 * implementation). No TUI scraping anywhere.
 */

/** Behavioral subset of the SDK `AgentSession` the worker depends on. */
export interface PiSessionLike {
  readonly sessionId: string;
  readonly sessionFile: string | undefined;
  readonly isStreaming: boolean;
  readonly messages: unknown[];
  subscribe(listener: (event: unknown) => void): () => void;
  prompt(
    text: string,
    options?: { streamingBehavior?: "steer" | "followUp" },
  ): Promise<void>;
  steer(text: string): Promise<void>;
  followUp(text: string): Promise<void>;
  abort(): Promise<void>;
}

/** Options the node supplies with `session.start`. */
export interface RootSessionConfig {
  /** Protocol session id the worker was started for. */
  sessionId: string;
  /** Repository/content working directory for the agent. */
  cwd: string;
  /** Application-controlled Pi agent directory; sessions persist under it. */
  agentDir: string;
  /** Optional model selector, e.g. "anthropic/claude-opus-4-5". */
  model?: string | undefined;
  /** Optional thinking level (off|minimal|low|medium|high|xhigh|max). */
  thinkingLevel?: string | undefined;
  /** Optional system prompt override; defaults to the root orchestration prompt. */
  systemPrompt?: string | undefined;
}

/** Seam over the official SDK session factory for deterministic tests. */
export interface PiSessionFactory {
  create(config: RootSessionConfig): Promise<PiSessionLike>;
}
