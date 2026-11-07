import request from 'mslx-request';

export async function putPluginData<T>(url: string, data: unknown): Promise<T> {
  const response = await request.put({ url, data }, { isReturnNativeResponse: true });
  const result = response?.data;
  if (result?.code !== 200 || result.data == null) {
    throw new Error(result?.message || '服务端返回了无效响应');
  }
  return result.data;
}
