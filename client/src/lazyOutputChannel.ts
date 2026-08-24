import type { OutputChannel, ViewColumn } from 'vscode';

export function createLazyOutputChannel(
  name: string,
  createChannel: () => OutputChannel
): OutputChannel {
  return new LazyOutputChannel(name, createChannel);
}

class LazyOutputChannel implements OutputChannel {
  private channel: OutputChannel | undefined;
  private disposed = false;

  public constructor(
    public readonly name: string,
    private readonly createChannel: () => OutputChannel
  ) {}

  public append(value: string): void {
    if (this.disposed) {
      return;
    }
    this.getChannel().append(value);
  }

  public appendLine(value: string): void {
    if (this.disposed) {
      return;
    }
    this.getChannel().appendLine(value);
  }

  public replace(value: string): void {
    if (this.disposed) {
      return;
    }
    this.getChannel().replace(value);
  }

  public clear(): void {
    this.channel?.clear();
  }

  public show(preserveFocus?: boolean): void;
  public show(column?: ViewColumn, preserveFocus?: boolean): void;
  public show(columnOrPreserveFocus?: ViewColumn | boolean, preserveFocus?: boolean): void {
    if (this.disposed) {
      return;
    }
    const channel = this.getChannel();
    if (typeof columnOrPreserveFocus === 'boolean') {
      channel.show(columnOrPreserveFocus);
      return;
    }
    channel.show(columnOrPreserveFocus, preserveFocus);
  }

  public hide(): void {
    this.channel?.hide();
  }

  public dispose(): void {
    this.disposed = true;
    this.channel?.dispose();
    this.channel = undefined;
  }

  private getChannel(): OutputChannel {
    this.channel ??= this.createChannel();
    return this.channel;
  }
}
