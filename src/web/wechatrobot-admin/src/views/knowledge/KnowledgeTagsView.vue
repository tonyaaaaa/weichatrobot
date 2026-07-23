<script setup lang="ts">
import { ElAlert } from 'element-plus';
</script>

<template>
  <section class="ops-page" aria-labelledby="tags-title">
    <header class="page-header"><div><p class="eyebrow">知识库运营</p><h1 id="tags-title">知识库标签</h1><p>标签决定群聊检索范围，不改变公共 OSS 文件本身的访问权限。</p></div></header>
    <section class="panel">
      <h2>匹配规则</h2>
      <div class="rule-explanation">
        <strong>任一标签匹配（OR）</strong>
        <p>群绑定多个标签时，文档命中其中任一标签即可参与检索。例如“技术部”绑定“产品、售后”，即可检索产品 OR 售后文档。</p>
        <strong>全局公开</strong>
        <p>标记为“全局公开”的知识无需群标签命中，所有已启用群都可以检索。</p>
      </div>
    </section>
    <section class="panel"><h2>标签管理</h2><ElAlert title="后端暂未提供标签维护 API" type="info" :closable="false" show-icon><p>索引接口可以消费已有标签 ID，但当前没有标签列表、新建、编辑或删除契约，因此本页保持只读说明状态。</p></ElAlert></section>
  </section>
</template>
