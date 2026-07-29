{{- define "tessera.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "tessera.fullname" -}}
{{- printf "%s-%s" .Release.Name (include "tessera.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "tessera.labels" -}}
app.kubernetes.io/name: {{ include "tessera.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "tessera.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "tessera.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}
