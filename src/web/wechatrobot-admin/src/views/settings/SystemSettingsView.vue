<script setup lang="ts">
import { ElAlert } from 'element-plus';
</script>

<template>
  <section class="ops-page" aria-labelledby="settings-title">
    <header class="page-header"><div><p class="eyebrow">平台治理</p><h1 id="settings-title">系统设置</h1><p>用于统一配置消息去重、限流、重试和转人工阈值。</p></div></header>
    <section class="panel">
      <ElAlert title="后端暂未提供系统设置读写 API" type="info" :closable="false" show-icon>
      <p>消息策略已有服务端实现，但尚无可审计、可回滚的运行时配置契约，因此当前保持只读不可用状态，不会显示“保存成功”。</p>
      </ElAlert>
    </section>
    <section class="panel">
      <h2>计划配置项</h2>
      <dl class="definition-grid"><dt>消息去重</dt><dd>同一外部消息只处理一次，避免机器人重复回复。</dd><dt>发送限流</dt><dd>控制短时间内发送频率，降低平台风控风险。</dd><dt>失败重试次数</dt><dd>临时网络或平台错误时允许的自动重试上限。</dd><dt>转人工阈值</dt><dd>低置信度、连续失败等条件达到阈值后暂停 AI 并通知人工。</dd></dl>
    </section>
  </section>
</template>
