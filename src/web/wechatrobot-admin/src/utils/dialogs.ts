import { ElMessageBox } from 'element-plus';

export interface ConfirmActionOptions {
  title?: string;
  confirmButtonText?: string;
  cancelButtonText?: string;
  danger?: boolean;
}

function isCancellation(error: unknown): boolean {
  if (error === 'cancel' || error === 'close') return true;
  const action = (error as { action?: unknown } | null)?.action;
  return action === 'cancel' || action === 'close';
}

export async function confirmAction(
  message: string,
  options: ConfirmActionOptions = {}
): Promise<boolean> {
  const danger = options.danger ?? /删除|停用|清除|拒绝|移除/.test(message);
  try {
    await ElMessageBox.confirm(message, options.title ?? '请确认', {
      type: 'warning',
      confirmButtonText: options.confirmButtonText ?? '确定',
      cancelButtonText: options.cancelButtonText ?? '取消',
      confirmButtonClass: danger ? 'el-button--danger' : undefined,
      distinguishCancelAndClose: true,
      closeOnClickModal: false
    });
    return true;
  } catch (error) {
    if (isCancellation(error)) return false;
    throw error;
  }
}

export async function promptAction(
  message: string,
  options: ConfirmActionOptions & { inputValue?: string; inputPattern?: RegExp; inputErrorMessage?: string } = {}
): Promise<string | null> {
  try {
    const result = await ElMessageBox.prompt(message, options.title ?? '请输入', {
      confirmButtonText: options.confirmButtonText ?? '确定',
      cancelButtonText: options.cancelButtonText ?? '取消',
      inputValue: options.inputValue,
      inputPattern: options.inputPattern,
      inputErrorMessage: options.inputErrorMessage,
      distinguishCancelAndClose: true,
      closeOnClickModal: false
    });
    return result.value;
  } catch (error) {
    if (isCancellation(error)) return null;
    throw error;
  }
}
