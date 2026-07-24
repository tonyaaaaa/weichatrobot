<script setup lang="ts">
import { ElAlert } from 'element-plus';
</script>

<template>
  <section class="ops-page" aria-labelledby="users-title">
    <header class="page-header"><div><p class="eyebrow">身份与权限</p><h1 id="users-title">用户与角色</h1><p>角色包括 Admin、KnowledgeOperator 和 HumanAgent。</p></div></header>
    <section class="panel">
      <ElAlert title="后端暂未提供用户与角色管理 API" type="info" :closable="false" show-icon>
      <p>当前身份系统可以认证和执行角色授权，但没有授权的用户列表、邀请、禁用或角色变更端点。本页不会把前端临时数据当作真实账号。</p>
      <p>如需变更角色，请先补充带审计日志和并发保护的管理员 API。</p>
      </ElAlert>
    </section>
  </section>
</template>
